using Android.App;
using Android.Content.PM;
using Android.OS;

namespace Nexora.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
        ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity
{
    private static int visible;
    private static readonly object visibilityGate = new();
    private static TaskCompletionSource resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static bool IsVisible => Volatile.Read(ref visible) != 0;

    internal static Task WaitUntilVisibleAsync(CancellationToken cancellationToken)
    {
        lock (visibilityGate)
            return IsVisible ? Task.CompletedTask : resumed.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
    }

    protected override void OnResume()
    {
        base.OnResume();
        lock (visibilityGate)
        {
            Volatile.Write(ref visible, 1);
            resumed.TrySetResult();
        }
    }

    protected override void OnPause()
    {
        lock (visibilityGate)
        {
            Volatile.Write(ref visible, 0);
            if (resumed.Task.IsCompleted) resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        base.OnPause();
    }
}
