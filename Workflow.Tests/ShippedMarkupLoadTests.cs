using System.Windows;
using Workflow.Views;

namespace Workflow.Tests;

/// <remarks>
/// <para>
/// Task 7.3, fourth observable: the views and the application resources still <em>load</em> after
/// the phase-completion surface was removed. The existing markup tests are all static -
/// <see cref="TaskTabViewXamlTests"/> reads <c>TaskTabView.xaml</c> as XML,
/// <see cref="AppResourceTests"/> checks that every <c>pack://</c> URI names a real manifest
/// resource, and <see cref="PhaseCompletionRemovalTests"/> scans the compiled assembly for
/// literals. None of them ever asks WPF to parse the compiled BAML, so the whole class of failure
/// that only the parser sees goes unobserved: a <c>{StaticResource}</c> whose key is not declared
/// anywhere throws <see cref="System.Windows.Markup.XamlParseException"/> on the first load and
/// takes the application down at startup, while every static test stays green because the
/// reference and the declaration sit in different files and neither one is individually wrong.
/// </para>
/// <para>
/// This test therefore does the one thing the others cannot: it builds the real
/// <see cref="Workflow.App"/> resource tree and constructs every shipped view against it, which is
/// the work <c>App.OnStartup</c> does before the first window appears.
/// </para>
/// <para>
/// One test rather than one per view, and its own class: WPF allows a single
/// <see cref="Application"/> per AppDomain and its <see cref="Application.Resources"/> have thread
/// affinity to the thread that built them, so every construction has to happen on the one STA
/// thread that created it. A theory would be given a fresh thread per case and would fail on that
/// affinity rather than on the markup. Each construction below is still an independent pin: an
/// unresolvable resource planted in any one view's markup fails this test on that view's line and
/// leaves the rest of the assembly green.
/// </para>
/// <para>
/// The single <see cref="Application"/> instance is a deliberate, process-wide side effect. No
/// other test in this assembly constructs a view or reads <see cref="Application.Current"/>; the
/// one production path that does, <c>MainWindowViewModel.CloseApplication</c>, is executed by no
/// test. The guard in <see cref="EnsureApplicationResources"/> keeps this idempotent if that ever
/// changes.
/// </para>
/// <para>
/// <c>MainWindow</c> is deliberately absent. Its icon is written as the assembly-less
/// <c>pack://application:,,,/Assets/workflow.ico</c>, which WPF resolves against
/// <see cref="Application.ResourceAssembly"/> - write-once, and already fixed to the test host
/// before any test runs, so the load fails on the host rather than on the markup. That exact URI
/// is what <see cref="AppResourceTests"/> checks by resolving the bare form against the
/// <c>Workflow</c> manifest, so the shell's one runtime-resolved reference is covered there.
/// </para>
/// </remarks>
public class ShippedMarkupLoadTests
{
    private static void EnsureApplicationResources()
    {
        if (Application.Current is not null)
        {
            return;
        }

        // Application.ResourceAssembly is not touched: it is write-once and the runtime has
        // already fixed it to the test host by the time any test runs. Nothing here needs it - the
        // generated loaders address their markup as "/Workflow;component/...", which names the
        // assembly and so resolves independently of it.
        var app = new App();
        app.InitializeComponent();
    }

    [StaFact]
    public void EveryShippedViewParsesItsCompiledMarkupAgainstTheApplicationResources()
    {
        EnsureApplicationResources();

        // Positive control: the application resource tree really was built, and really did merge
        // its dictionaries rather than only its inline block. Without this, every assertion below
        // could be satisfied by markup that happens to name no resource at all.
        var resources = Application.Current?.Resources;
        Assert.NotNull(resources);
        Assert.NotNull(resources["PhaseStatusToBrushConverter"]);
        Assert.NotNull(resources["MaterialDesignRaisedButton"]);

        // Constructing a view runs its generated InitializeComponent, which is where WPF resolves
        // every StaticResource, style and converter reference its markup names. Each line is its
        // own pin on its own file.
        Assert.NotNull(new PhaseIndicatorView());
        Assert.NotNull(new SubtaskIndicatorView());
        Assert.NotNull(new TerminalView());
        Assert.NotNull(new TaskTabView());
    }
}
