using System.Windows;

using OhMyLibrary.Tests.Infrastructure;

using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace OhMyLibrary.Tests.Performance;

/// <summary>
/// One STA dispatcher for the whole process, with the shell's resource dictionaries loaded on it.
/// </summary>
/// <remarks>
/// <para>
/// WPF allows a single <see cref="Application"/> per process and a view model binds itself to
/// <c>Application.Current.Dispatcher</c>, so a per-test dispatcher would leave every test after the
/// first posting its work to a thread that is no longer pumping. One shared thread is the only
/// arrangement that keeps them all honest.
/// </para>
/// <para>
/// A page built without the dictionaries resolves none of its <c>StaticResource</c> keys. Only the
/// dictionaries are shared with the running shell — no host, no logging, no window is created, and
/// nothing is ever shown, so no test can steal focus.
/// </para>
/// </remarks>
public static class WpfApplicationFixture
{
    private static readonly Lazy<StaDispatcher> Instance = new(Create, isThreadSafe: true);

    /// <summary>The shared dispatcher, with <see cref="Application.Current"/> already on it.</summary>
    public static StaDispatcher Dispatcher => Instance.Value;

    private static StaDispatcher Create()
    {
        var sta = StaDispatcher.Start("oml-wpf-fixture");

        sta.InvokeAsync(() =>
        {
            if (Application.Current is not null)
            {
                return;
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
            app.Resources.MergedDictionaries.Add(new ControlsDictionary());
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/OhMyLibrary;component/Styles/Theme.xaml", UriKind.Absolute),
            });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/OhMyLibrary;component/Styles/Cards.xaml", UriKind.Absolute),
            });
        }).GetAwaiter().GetResult();

        return sta;
    }
}
