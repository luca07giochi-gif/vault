using UIKit;

namespace vault.iOS
{
    public sealed class ExtraViewController : UIViewController
    {
        public override void ViewDidLoad()
        {
            base.ViewDidLoad();

            Title = "Extra";
            View!.BackgroundColor = UIColor.White;

            UIButton settingsButton = UIButton.FromType(UIButtonType.System);
            settingsButton.SetImage(UIImage.GetSystemImage("gearshape.fill"), UIControlState.Normal);
            settingsButton.TintColor = UIColor.FromRGB(10, 132, 255);
            settingsButton.AccessibilityLabel = "Impostazioni analisi Instagram";
            settingsButton.TouchUpInside += (_, _) => OpenInstagramAnalysisSettings();
            NavigationItem.RightBarButtonItem = new UIBarButtonItem(settingsButton);

            var heading = new UILabel
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Text = "Funzionalità aggiuntive",
                Font = UIFont.SystemFontOfSize(22, UIFontWeight.Bold),
                TextColor = UIColor.Black
            };

            var description = new UILabel
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                Text = "Strumenti per analizzare e gestire i tuoi dati.",
                Font = UIFont.SystemFontOfSize(15),
                TextColor = UIColor.DarkGray,
                Lines = 0
            };

            var instagramButton = UIButton.FromType(UIButtonType.System);
            instagramButton.TranslatesAutoresizingMaskIntoConstraints = false;
            instagramButton.SetTitle("Analisi Instagram", UIControlState.Normal);
            instagramButton.SetTitleColor(UIColor.FromRGB(10, 132, 255), UIControlState.Normal);
            instagramButton.TitleLabel!.Font = UIFont.SystemFontOfSize(17, UIFontWeight.Semibold);
            instagramButton.HorizontalAlignment = UIControlContentHorizontalAlignment.Left;
            instagramButton.BackgroundColor = UIColor.FromRGB(245, 245, 250);
            instagramButton.Layer.CornerRadius = 12f;
            instagramButton.AccessibilityLabel = "Apri analisi Instagram";

            UIImage? instagramImage = UIImage.GetSystemImage("person.2");
            if (instagramImage != null)
                instagramButton.SetImage(instagramImage, UIControlState.Normal);
            instagramButton.TouchUpInside += (_, _) => OpenInstagramAnalysis();

            View.AddSubviews(heading, description, instagramButton);
            NSLayoutConstraint.ActivateConstraints(new NSLayoutConstraint[]
            {
                heading.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor, 24),
                heading.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor, 20),
                heading.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor, -20),
                description.TopAnchor.ConstraintEqualTo(heading.BottomAnchor, 8),
                description.LeadingAnchor.ConstraintEqualTo(heading.LeadingAnchor),
                description.TrailingAnchor.ConstraintEqualTo(heading.TrailingAnchor),
                instagramButton.TopAnchor.ConstraintEqualTo(description.BottomAnchor, 24),
                instagramButton.LeadingAnchor.ConstraintEqualTo(heading.LeadingAnchor),
                instagramButton.TrailingAnchor.ConstraintEqualTo(heading.TrailingAnchor),
                instagramButton.HeightAnchor.ConstraintEqualTo(64)
            });
        }

        private void OpenInstagramAnalysis()
        {
            var instagramViewController = new InstagramAnalysisViewController();
            if (NavigationController != null)
            {
                NavigationController.PushViewController(instagramViewController, true);
                return;
            }

            var navigationController = new UINavigationController(instagramViewController)
            {
                ModalPresentationStyle = UIModalPresentationStyle.FullScreen
            };
            PresentViewController(navigationController, true, null);
        }

        private void OpenInstagramAnalysisSettings()
        {
            var settingsViewController = new InstagramAnalysisSettingsViewController();
            if (NavigationController != null)
            {
                NavigationController.PushViewController(settingsViewController, true);
                return;
            }

            var navigationController = new UINavigationController(settingsViewController)
            {
                ModalPresentationStyle = UIModalPresentationStyle.FullScreen
            };
            PresentViewController(navigationController, true, null);
        }
    }
}
