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
            vm.Home.OnShown();
            // first start (or not accepted yet): the disclaimer pops up over the app until it is accepted
            desktop.MainWindow.Opened += async (_, _) =>
                await DisclaimerGate.EnsureAcceptedAsync(() => { desktop.Shutdown(); return Task.CompletedTask; });
            // Debug aid: SYN3_PAGE=Settings opens that page first.
            if (Environment.GetEnvironmentVariable("SYN3_PAGE") is { Length: > 0 } page &&
                vm.Pages.FirstOrDefault(x => x.GetType().Name.StartsWith(page, StringComparison.OrdinalIgnoreCase)) is { } target)
                vm.Current = target;
        }
        _ = Ctx.RandomizeUserAgentAsync();
        base.OnFrameworkInitializationCompleted();
    }
}
