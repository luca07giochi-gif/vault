using Foundation;
using UIKit;

namespace vault.iOS
{
    [Register("AppDelegate")]
    public sealed class AppDelegate : UIApplicationDelegate
    {
        public override UIWindow? Window { get; set; }

        public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
        {
            Window = new UIWindow(UIScreen.MainScreen.Bounds)
            {
                RootViewController = new UINavigationController(new MainViewController())
            };
            Window.MakeKeyAndVisible();
            return true;
        }
    }
}
