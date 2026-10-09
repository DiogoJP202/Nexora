namespace Nexora.Mobile;

public sealed class App : Application
{
    private readonly MobileWorkspace workspace;

    public App(MobileWorkspace workspace)
    {
        this.workspace = workspace;
        UserAppTheme = AppTheme.Dark;
        Resources.Add(new Style(typeof(Label))
        {
            Setters = { new Setter { Property = Label.TextColorProperty, Value = Palette.Text },
                new Setter { Property = Label.FontFamilyProperty, Value = "sans-serif" } }
        });
        Resources.Add(new Style(typeof(Button))
        {
            Setters = { new Setter { Property = Button.CornerRadiusProperty, Value = 14 },
                new Setter { Property = Button.BackgroundColorProperty, Value = Palette.Accent },
                new Setter { Property = Button.TextColorProperty, Value = Palette.Background },
                new Setter { Property = Button.FontAttributesProperty, Value = FontAttributes.Bold },
                new Setter { Property = Button.PaddingProperty, Value = new Thickness(18, 12) } }
        });
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(new LoginPage(workspace));
}

internal static class Palette
{
    internal static readonly Color Background = Color.FromArgb("#0B1220");
    internal static readonly Color Surface = Color.FromArgb("#172238");
    internal static readonly Color Accent = Color.FromArgb("#A499FF");
    internal static readonly Color Mint = Color.FromArgb("#69E6C8");
    internal static readonly Color Text = Color.FromArgb("#EDF1FA");
    internal static readonly Color Muted = Color.FromArgb("#A6B4CD");
}
