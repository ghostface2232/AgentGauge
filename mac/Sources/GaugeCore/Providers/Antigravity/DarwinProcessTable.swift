import Darwin
import Foundation

/// What `KERN_PROCARGS2` reports for a process: the exec path, argv with its original
/// element boundaries (an install path with spaces stays one argument), and the environment.
struct ProcessArguments: Equatable {
    var executable: String
    var arguments: [String]
    /// Nil when the kernel returned no environment records (it may omit them), which is not
    /// the same as an empty environment.
    var environment: [String]?
}

/// Read-only process inspection through libproc and sysctl, so discovery never spawns `ps`
/// or `lsof` on every refresh. Every call tolerates a process that exits mid-read (or one we
/// may not inspect) by returning nil/empty rather than throwing.
enum DarwinProcessTable {
    static func allPIDs() -> [pid_t] {
        let required = proc_listallpids(nil, 0)
        guard required > 0 else { return [] }
        // Headroom: processes started between the sizing call and the read.
        var pids = [pid_t](repeating: 0, count: Int(required) + 64)
        let count = pids.withUnsafeMutableBytes { proc_listallpids($0.baseAddress, Int32($0.count)) }
        guard count > 0 else { return [] }
        return pids.prefix(Int(count)).filter { $0 > 0 }
    }

    static func executablePath(_ pid: pid_t) -> String? {
        var buffer = [CChar](repeating: 0, count: Int(MAXPATHLEN) * 4)
        let length = buffer.withUnsafeMutableBytes { proc_pidpath(pid, $0.baseAddress, UInt32($0.count)) }
        guard length > 0 else { return nil }
        return String(decoding: buffer.prefix(Int(length)).map { UInt8(bitPattern: $0) }, as: UTF8.self)
    }

    /// The process start time in microseconds since the epoch. Paired with the PID it is the
    /// process's identity: PIDs are reused, start times of a reused PID differ.
    static func startTime(_ pid: pid_t) -> UInt64? {
        var info = proc_bsdinfo()
        let size = Int32(MemoryLayout<proc_bsdinfo>.size)
        guard proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, size) == size else { return nil }
        return info.pbi_start_tvsec * 1_000_000 + info.pbi_start_tvusec
    }

    static func uid(_ pid: pid_t) -> uid_t? {
        var info = proc_bsdinfo()
        let size = Int32(MemoryLayout<proc_bsdinfo>.size)
        guard proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, size) == size else { return nil }
        return info.pbi_uid
    }

    /// argv and environment of a same-user process; nil for one we may not inspect.
    static func arguments(_ pid: pid_t) -> ProcessArguments? {
        var mib: [Int32] = [CTL_KERN, KERN_PROCARGS2, pid]
        var size = 0
        guard sysctl(&mib, u_int(mib.count), nil, &size, nil, 0) == 0, size >= MemoryLayout<Int32>.size else {
            return nil
        }
        var data = Data(count: size)
        let result = data.withUnsafeMutableBytes { sysctl(&mib, u_int(mib.count), $0.baseAddress, &size, nil, 0) }
        guard result == 0 else { return nil }
        return parseProcArgs2(data.prefix(size))
    }

    /// Parses a `KERN_PROCARGS2` buffer: an Int32 argc, the exec path, NUL padding, argc
    /// NUL-terminated arguments, then NUL-terminated `KEY=value` strings ended by an empty one.
    static func parseProcArgs2(_ data: Data) -> ProcessArguments? {
        let bytes = [UInt8](data)
        guard bytes.count >= 4 else { return nil }
        let argc = Int(Int32(littleEndian: bytes.withUnsafeBytes { $0.loadUnaligned(as: Int32.self) }))
        guard argc >= 0, argc <= bytes.count else { return nil }

        var offset = 4
        func nextString() -> String? {
            guard offset < bytes.count, let end = bytes[offset...].firstIndex(of: 0) else { return nil }
            let value = String(decoding: bytes[offset..<end], as: UTF8.self)
            offset = end + 1
            return value
        }

        guard let executable = nextString() else { return nil }
        while offset < bytes.count, bytes[offset] == 0 { offset += 1 }

        var arguments: [String] = []
        arguments.reserveCapacity(argc)
        for _ in 0..<argc {
            guard let argument = nextString() else { return nil }
            arguments.append(argument)
        }

        var environment: [String]? = nil
        if offset < bytes.count, bytes[offset] != 0 {
            var entries: [String] = []
            while offset < bytes.count, bytes[offset] != 0, let entry = nextString() {
                entries.append(entry)
            }
            environment = entries
        }
        return ProcessArguments(executable: executable, arguments: arguments, environment: environment)
    }

    /// TCP ports the process is listening on at exactly 127.0.0.1 — the only address the
    /// loopback client connects to and the only one its TLS exception covers.
    static func loopbackListeningPorts(_ pid: pid_t) -> [Int] {
        let required = proc_pidinfo(pid, PROC_PIDLISTFDS, 0, nil, 0)
        guard required > 0 else { return [] }
        let stride = MemoryLayout<proc_fdinfo>.stride
        var descriptors = [proc_fdinfo](repeating: proc_fdinfo(), count: Int(required) / stride + 16)
        let filled = descriptors.withUnsafeMutableBytes {
            proc_pidinfo(pid, PROC_PIDLISTFDS, 0, $0.baseAddress, Int32($0.count))
        }
        guard filled > 0 else { return [] }

        var ports = Set<Int>()
        for descriptor in descriptors.prefix(Int(filled) / stride) where descriptor.proc_fdtype == PROX_FDTYPE_SOCKET {
            var info = socket_fdinfo()
            let size = Int32(MemoryLayout<socket_fdinfo>.size)
            guard proc_pidfdinfo(pid, descriptor.proc_fd, PROC_PIDFDSOCKETINFO, &info, size) == size,
                  info.psi.soi_kind == SOCKINFO_TCP,
                  info.psi.soi_proto.pri_tcp.tcpsi_state == TSI_S_LISTEN else { continue }
            let endpoint = info.psi.soi_proto.pri_tcp.tcpsi_ini
            guard endpoint.insi_vflag & UInt8(INI_IPV4) != 0,
                  UInt32(bigEndian: endpoint.insi_laddr.ina_46.i46a_addr4.s_addr) == 0x7F00_0001 else { continue }
            ports.insert(Int(UInt16(bigEndian: UInt16(truncatingIfNeeded: endpoint.insi_lport))))
        }
        return ports.sorted()
    }
}
