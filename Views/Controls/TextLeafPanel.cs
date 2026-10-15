using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Gauge.Views.Controls;

/// <summary>
/// A horizontal <see cref="StackPanel"/> that UI Automation sees as a single Text leaf
/// named by <c>AutomationProperties.Name</c>, with its own children (an icon glyph, a
/// short label) left out. A chip needs this when it sits in a <see cref="SquircleBorder"/>:
/// the border is Raw, and WinUI ships no automation peer for panels or icons, so once the
/// short label collapses the chip would otherwise have nothing a screen reader can reach.
/// </summary>
public sealed partial class TextLeafPanel : StackPanel
{
    public TextLeafPanel()
    {
        Orientation = Orientation.Horizontal;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TextLeafPanelAutomationPeer(this);

    private sealed class TextLeafPanelAutomationPeer(TextLeafPanel owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(TextLeafPanel);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        // The name already carries the spelled-out text; the glyph and short label would only repeat it.
        protected override IList<AutomationPeer> GetChildrenCore() => new List<AutomationPeer>();
    }
}
