using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.App.ViewModels;
using Cyanlabs.Syn3Updater.App.Views;

namespace Cyanlabs.Syn3Updater.App;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Ctx.Init();
        SettingsViewModel.ApplyTheme(Ctx.Main.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.Exit += (_, _) =>
            {
                Ctx.Save();
                Ctx.Log.Save(Ctx.Paths.AppLogFile);
            };
            _ = vm.LoadAdAsync();
            _ = vm.CheckForUpdateAsync();
            vm.StartUpdateWatcher();
            UrlSchemeRegistration.EnsureRegistered();
            vm.Home.OnShown();
            // first start (or not accepted yet): the disclaimer pops up over the app until it is accepted
            desktop.MainWindow.Opened += async (_, _) =>
                { if (await DisclaimerGate.EnsureAcceptedAsync(() => { desktop.Shutdown(); return Task.CompletedTask; })) { await vm.EnforceMinimumAsync(); await CrashReports.OfferAsync(); } };
            // syn3updater:// links: the one that started the app, ones forwarded by a second launch, and (macOS) ones the OS delivers while it runs
            desktop.MainWindow.Opened += async (_, _) => { if (AppLaunch.StartupLink is { } link) await UrlIntents.HandleAsync(link); };
            desktop.MainWindow.Opened += async (_, _) => await vm.OnStartedAsync();       // after an update: What's new, and clean-up
            AppLaunch.OnMessage = message => Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                if (desktop.MainWindow is { } w) { if (w.WindowState == Avalonia.Controls.WindowState.Minimized) w.WindowState = Avalonia.Controls.WindowState.Normal; w.Activate(); }
                if (message != "activate") await UrlIntents.HandleAsync(message);
            });
            AppLaunch.Primary?.Listen(AppLaunch.OnMessage);
            // macOS delivers links as OS events. Avalonia raises the older UrlsOpened first with the raw text, then (only if its storage provider is available) Activated; listen to both,
            // and drop the repeat when both fire for one link. A macOS log of 3.0.0-test26 showed no Activated event at all, so UrlsOpened is the one that must work.
            var lastLink = ""; var lastAt = DateTime.MinValue;
            void OnOsLink(string link, string via)
            {
                Ctx.Log.Info($"Link received from the OS ({via}): {link}");
                if (link == lastLink && DateTime.UtcNow - lastAt < TimeSpan.FromSeconds(3)) return;
                lastLink = link; lastAt = DateTime.UtcNow;
                _ = UrlIntents.HandleAsync(link);
            }
#pragma warning disable CS0618 // UrlsOpened is marked obsolete in favour of Activated, but it is the event that fires reliably on macOS
            UrlsOpened += (_, e) => { foreach (var u in e.Urls) if (Cyanlabs.Syn3Updater.Core.Services.UrlIntentParser.Find(new[] { u }) is { } link) OnOsLink(link, "UrlsOpened"); };
#pragma warning restore CS0618
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                activatable.Activated += (_, e) =>
                    { if (e is ProtocolActivatedEventArgs { Kind: ActivationKind.OpenUri } p) OnOsLink(p.Uri.OriginalString, "Activated"); };
            desktop.Exit += (_, _) => AppLaunch.Primary?.Dispose();
            // Debug aid: SYN3_PAGE=Settings opens that page first.
            if (Environment.GetEnvironmentVariable("SYN3_PAGE") is { Length: > 0 } page &&
                vm.Pages.FirstOrDefault(x => x.GetType().Name.StartsWith(page, StringComparison.OrdinalIgnoreCase)) is { } target)
                vm.Current = target;
        }
        _ = Ctx.RandomizeUserAgentAsync();
        base.OnFrameworkInitializationCompleted();
    }
}
