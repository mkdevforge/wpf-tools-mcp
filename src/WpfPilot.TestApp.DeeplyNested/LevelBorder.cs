using System.Collections.Generic;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace WpfPilot.TestApp.DeeplyNested;

public sealed class LevelBorder : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new LevelBorderAutomationPeer(this);

    private sealed class LevelBorderAutomationPeer : FrameworkElementAutomationPeer
    {
        public LevelBorderAutomationPeer(LevelBorder owner)
            : base(owner)
        {
        }

        protected override string GetClassNameCore() => nameof(LevelBorder);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;

        protected override bool IsControlElementCore() => true;

        protected override bool IsContentElementCore() => true;

        protected override List<AutomationPeer>? GetChildrenCore()
        {
            var border = (LevelBorder)Owner;
            if (border.Child is null)
            {
                return null;
            }

            var childPeer = UIElementAutomationPeer.CreatePeerForElement(border.Child);
            if (childPeer is null)
            {
                return null;
            }

            return new List<AutomationPeer> { childPeer };
        }
    }
}

