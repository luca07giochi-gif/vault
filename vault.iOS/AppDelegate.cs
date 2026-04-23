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

        public override bool OpenUrl(UIApplication app, NSUrl url, NSDictionary options)
        {
            MainViewController? controller = ResolveMainViewController();
            return controller?.HandleIncomingVaultUrl(url) ?? false;
        }

        private MainViewController? ResolveMainViewController()
        {
            if (Window?.RootViewController is UINavigationController nav)
            {
                UIViewController[]? controllers = nav.ViewControllers;
                if (controllers != null)
                {
                    return controllers.OfType<MainViewController>().FirstOrDefault()
                        ?? nav.TopViewController as MainViewController;
                }

                return nav.TopViewController as MainViewController;
            }

            return Window?.RootViewController as MainViewController;
        }
    }
}
