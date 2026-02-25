using System.Linq;
using Foundation;
using UIKit;
using vault.Core.Domain;

namespace vault.iOS
{
    [Register("AppDelegate")]
    public sealed class AppDelegate : UIApplicationDelegate
    {
        public override UIWindow? Window { get; set; }

        public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
        {
            VaultPortableReader.CleanupStaleTemporarySessions();
            MainViewController.CleanupStaleRuntimeTemporaryFiles();

            Window = new UIWindow(UIScreen.MainScreen.Bounds)
            {
                RootViewController = new UINavigationController(new MainViewController())
            };
            Window.MakeKeyAndVisible();
            return true;
        }

        public override void DidEnterBackground(UIApplication application)
        {
            ResolveMainViewController()?.OnAppDidEnterBackground();
        }

        public override void WillTerminate(UIApplication application)
        {
            ResolveMainViewController()?.OnAppWillTerminate();
            VaultPortableReader.CleanupStaleTemporarySessions();
            MainViewController.CleanupStaleRuntimeTemporaryFiles();
        }

        private MainViewController? ResolveMainViewController()
        {
            if (Window?.RootViewController is UINavigationController nav)
            {
                return nav.ViewControllers.OfType<MainViewController>().FirstOrDefault()
                    ?? nav.TopViewController as MainViewController;
            }

            return Window?.RootViewController as MainViewController;
        }
    }
}
