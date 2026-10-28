using System.Drawing;
using System.Globalization;

namespace Gauge.Services;

/// <summary>One closed or open subpath: a start point followed by cubic Bézier segments.</summary>
public sealed record SvgFigure(PointF Start, IReadOnlyList<(PointF C1, PointF C2, PointF End)> Segments, bool Closed);

/// <summary>
/// Minimal SVG path-data reader for the tray logos (Assets\ToolLogos). It covers the
/// commands those files and typical icon exports use — M L H V C S Q T Z, absolute and
/// relative — and turns everything into cubic segments so one drawing path serves all of
/// them. Arcs (A) are not supported: a logo using one fails to parse and the ring is drawn
/// without it rather than with a wrong shape.
/// </summary>
public static class SvgPathData
{
    public static IReadOnlyList<SvgFigure> Parse(string data)
    {
        var tokens = Tokenize(data);
        var figures = new List<SvgFigure>();
        var segments = new List<(PointF, PointF, PointF)>();
        PointF start = default, current = default, lastControl = default;
        var hasFigure = false;
        var previous = ' ';
        var i = 0;
        var command = ' ';

        void Flush(bool closed)
        {
            if (hasFigure) figures.Add(new SvgFigure(start, segments.ToArray(), closed));
            segments.Clear();
            hasFigure = false;
        }

        void Line(PointF to)
        {
            segments.Add((current, to, to));
            current = to;
        }

        float Next()
        {
            if (i >= tokens.Count || tokens[i] is not float value)
                throw new FormatException("SVG path data ended where a number was expected.");
            i++;
            return value;
        }

        while (i < tokens.Count)
        {
            if (tokens[i] is char c)
            {
                command = c;
                i++;
            }
            else if (command == ' ')
            {
                throw new FormatException("SVG path data has a number with no command before it.");
            }

            var relative = char.IsLower(command);
            var origin = relative ? current : PointF.Empty;
            PointF Point() { var x = Next(); var y = Next(); return new PointF(origin.X + x, origin.Y + y); }

            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    Flush(false);
                    start = current = Point();
                    hasFigure = true;
                    // Coordinates following a moveto are implicit linetos.
                    command = relative ? 'l' : 'L';
                    break;
                case 'L':
                    Line(Point());
                    break;
                case 'H':
                    Line(new PointF((relative ? current.X : 0) + Next(), current.Y));
                    break;
                case 'V':
                    Line(new PointF(current.X, (relative ? current.Y : 0) + Next()));
                    break;
                case 'C':
                {
                    var c1 = Point(); var c2 = Point(); var end = Point();
                    segments.Add((c1, c2, end));
                    lastControl = c2; current = end;
                    break;
                }
                case 'S':
                {
                    var c1 = "CcSs".Contains(previous) ? Reflect(lastControl, current) : current;
                    var c2 = Point(); var end = Point();
                    segments.Add((c1, c2, end));
                    lastControl = c2; current = end;
                    break;
                }
                case 'Q':
                {
                    var q = Point(); var end = Point();
                    segments.Add(QuadraticToCubic(current, q, end));
                    lastControl = q; current = end;
                    break;
                }
                case 'T':
                {
                    var q = "QqTt".Contains(previous) ? Reflect(lastControl, current) : current;
                    var end = Point();
                    segments.Add(QuadraticToCubic(current, q, end));
                    lastControl = q; current = end;
                    break;
                }
                case 'Z':
                    Flush(true);
                    current = start;
                    // Z takes no arguments, so a number after it has no command to belong to.
                    previous = command;
                    command = ' ';
                    continue;
                default:
                    throw new FormatException($"Unsupported SVG path command '{command}'.");
            }

            previous = command;
        }

        Flush(false);
        return figures;
    }

    private static PointF Reflect(PointF control, PointF about)
        => new(2 * about.X - control.X, 2 * about.Y - control.Y);

    private static (PointF, PointF, PointF) QuadraticToCubic(PointF from, PointF q, PointF to)
        => (new PointF(from.X + 2f / 3 * (q.X - from.X), from.Y + 2f / 3 * (q.Y - from.Y)),
            new PointF(to.X + 2f / 3 * (q.X - to.X), to.Y + 2f / 3 * (q.Y - to.Y)),
            to);

    private static List<object> Tokenize(string data)
    {
        var tokens = new List<object>();
        var i = 0;
        while (i < data.Length)
        {
            var c = data[i];
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
            if (char.IsLetter(c) && c is not ('e' or 'E')) { tokens.Add(c); i++; continue; }

            // A number ends at a second sign (not after an exponent) or a second decimal
            // point, so "-1.5.5-2" reads as -1.5, .5, -2.
            var begin = i;
            if (data[i] is '+' or '-') i++;
            var seenDot = false;
            while (i < data.Length)
            {
                var d = data[i];
                if (char.IsDigit(d)) { i++; continue; }
                if (d == '.' && !seenDot) { seenDot = true; i++; continue; }
                if (d is 'e' or 'E')
                {
                    i++;
                    if (i < data.Length && data[i] is '+' or '-') i++;
                    continue;
                }
                break;
            }

            if (i == begin) throw new FormatException($"Unexpected character '{c}' in SVG path data.");
            tokens.Add(float.Parse(data.AsSpan(begin, i - begin), NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        return tokens;
    }
}
