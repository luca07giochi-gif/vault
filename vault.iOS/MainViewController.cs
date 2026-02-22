using CoreGraphics;
using UIKit;

namespace vault.iOS
{
    public sealed class MainViewController : UIViewController
    {
        public override void ViewDidLoad()
        {
            base.ViewDidLoad();

            View!.BackgroundColor = UIColor.White;

            var titleLabel = new UILabel(new CGRect(24, 90, View.Bounds.Width - 48, 36))
            {
                Text = "Cassaforte iOS",
                Font = UIFont.BoldSystemFontOfSize(28),
                TextColor = UIColor.Black
            };

            var statusLabel = new UILabel(new CGRect(24, 140, View.Bounds.Width - 48, 120))
            {
                Text = "Bootstrap iOS separato da desktop/web.\nProssimo step: apertura vault e navigazione file.",
                Font = UIFont.SystemFontOfSize(17),
                TextColor = UIColor.DarkGray,
                Lines = 0
            };

            View.AddSubview(titleLabel);
            View.AddSubview(statusLabel);
        }
    }
}
