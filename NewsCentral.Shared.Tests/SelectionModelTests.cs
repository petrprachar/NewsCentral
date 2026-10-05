using NewsCentral.Ui;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class SelectionModelTests
{
    [Fact]
    public void StateOf_EmptyShown_ReturnsNone()
    {
        var model = new SelectionModel<string>();

        Assert.Equal(SelectionState.None, model.StateOf(Array.Empty<string>()));
    }

    [Fact]
    public void StateOf_NoneOfShownSelected_ReturnsNone()
    {
        var model = new SelectionModel<string>();

        Assert.Equal(SelectionState.None, model.StateOf(new[] { "a", "b" }));
    }

    [Fact]
    public void StateOf_AllOfShownSelected_ReturnsAll()
    {
        var model = new SelectionModel<string>();
        model.Set("a", true);
        model.Set("b", true);

        Assert.Equal(SelectionState.All, model.StateOf(new[] { "a", "b" }));
    }

    [Fact]
    public void StateOf_SomeOfShownSelected_ReturnsSome()
    {
        var model = new SelectionModel<string>();
        model.Set("a", true);

        Assert.Equal(SelectionState.Some, model.StateOf(new[] { "a", "b" }));
    }

    [Fact]
    public void ToggleAll_FromNone_SelectsAllShown()
    {
        var model = new SelectionModel<string>();

        model.ToggleAll(new[] { "a", "b" });

        Assert.True(model.IsSelected("a"));
        Assert.True(model.IsSelected("b"));
        Assert.Equal(2, model.Count);
    }

    [Fact]
    public void ToggleAll_FromSome_SelectsAllShown()
    {
        var model = new SelectionModel<string>();
        model.Set("a", true);

        model.ToggleAll(new[] { "a", "b" });

        Assert.True(model.IsSelected("a"));
        Assert.True(model.IsSelected("b"));
    }

    [Fact]
    public void ToggleAll_FromAll_DeselectsAllShown()
    {
        var model = new SelectionModel<string>();
        model.Set("a", true);
        model.Set("b", true);

        model.ToggleAll(new[] { "a", "b" });

        Assert.False(model.IsSelected("a"));
        Assert.False(model.IsSelected("b"));
        Assert.Equal(0, model.Count);
    }

    [Fact]
    public void ToggleAll_OverASubset_NeverTouchesHiddenSelections()
    {
        // "c" is selected but not part of the shown subset — ToggleAll over {a, b} must leave it alone.
        var model = new SelectionModel<string>();
        model.Set("c", true);

        model.ToggleAll(new[] { "a", "b" });

        Assert.True(model.IsSelected("a"));
        Assert.True(model.IsSelected("b"));
        Assert.True(model.IsSelected("c"));

        model.ToggleAll(new[] { "a", "b" });

        Assert.False(model.IsSelected("a"));
        Assert.False(model.IsSelected("b"));
        Assert.True(model.IsSelected("c")); // untouched throughout
    }

    [Fact]
    public void RemoveMissing_DropsSelectedKeysNoLongerPresent()
    {
        var model = new SelectionModel<string>();
        model.Set("a", true);
        model.Set("b", true);
        model.Set("c", true);

        model.RemoveMissing(new[] { "a", "c" });

        Assert.True(model.IsSelected("a"));
        Assert.False(model.IsSelected("b"));
        Assert.True(model.IsSelected("c"));
        Assert.Equal(2, model.Count);
    }

    [Fact]
    public void Clear_RemovesEverySelection()
    {
        var model = new SelectionModel<string>();
        model.Set("a", true);
        model.Set("b", true);

        model.Clear();

        Assert.Equal(0, model.Count);
    }
}
