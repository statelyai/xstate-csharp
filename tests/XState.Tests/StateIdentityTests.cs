using Xunit;

namespace XState.Tests;

/// <summary>State and actor identity parity with xstate v6 (#5810, #5814).</summary>
public sealed class StateIdentityTests
{
    [Fact]
    public void AnonymousMachine_ImplicitIdsHaveNoPrefix()
    {
        var machine = Machine.Create("")
            .Initial("a")
            .State("a", s => s.Initial("a1").State("a1"))
            .Build();

        Assert.Equal("", machine.Root.Id);
        Assert.Equal("a", machine.GetNodeById("a").Id);
        Assert.Equal("a.a1", machine.GetNodeById("a.a1").Id);
        Assert.True(machine.GetInitialState().State.Matches("#a.a1"));
    }

    [Fact]
    public void AnonymousMachine_RootCannotBeHashTargeted()
    {
        var error = Assert.Throws<ArgumentException>(() => Machine.Create("")
            .Initial("a")
            .State("a", s => s.On<Go>(t => t.Target("#")))
            .Build());

        Assert.Contains("cannot be referenced by id", error.Message);
    }

    [Fact]
    public void UnnamedInvokeId_IsStateIdColonIndex()
    {
        var machine = Machine.Create("shop")
            .Initial("loading")
            .State("loading", s => s.Invoke(new StubLogic()).Invoke(new StubLogic()))
            .Build();

        Assert.Equal(["shop.loading:0", "shop.loading:1"], machine.GetNodeById("shop.loading").Invokes.Select(i => i.Id));
    }

    [Fact]
    public void RootInvokeId_IsColonZeroOrMachineIdColonZero()
    {
        var anonymous = Machine.Create("").Root(r => r.Invoke(new StubLogic())).Initial("a").State("a").Build();
        var named = Machine.Create("shop").Root(r => r.Invoke(new StubLogic())).Initial("a").State("a").Build();

        Assert.Equal(":0", Assert.Single(anonymous.Root.Invokes).Id);
        Assert.Equal("shop:0", Assert.Single(named.Root.Invokes).Id);
    }

    [Fact]
    public void SpawnId_SkipsIdHeldByInvoke()
    {
        var child = Machine.Create("child").Initial("idle").State("idle").Build();
        var machine = Machine.Create("host")
            .Initial("a")
            .State("a", s => s
                .Invoke(new StubLogic(), "child:0")
                .On<Go>(t => t.Do(new SpawnAction<Unit>(child))))
            .Build();

        var result = machine.Transition(machine.GetInitialState().State, new Go());

        Assert.Equal("child:1", Assert.Single(result.Effects.OfType<SpawnEffect>()).Id);
    }

    [Fact]
    public void DuplicateExplicitStateId_Throws()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Machine.Create("m")
            .Initial("a")
            .State("a", s => s.Id("same"))
            .State("b", s => s.Id("same"))
            .Build());

        Assert.Equal(
            "Duplicate state id 'same': the states at 'a' and 'b' both have it. State ids must be unique; rename the explicit `id`.",
            error.Message);
    }

    [Fact]
    public void ExplicitIdEqualToImplicitId_Throws()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Machine.Create("m")
            .Initial("a")
            .State("a")
            .State("b", s => s.Id("m.a"))
            .Build());

        Assert.StartsWith("Duplicate state id 'm.a'", error.Message);
    }

    [Fact]
    public void StateKeyWithDot_Throws()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Machine.Create("m")
            .Initial("foo.bar")
            .State("foo.bar")
            .Build());

        Assert.Equal("State key \"foo.bar\" contains \".\" — nest the states or escape it as \"foo\\\\.bar\".", error.Message);
    }

    [Fact]
    public void EscapedDotKey_RoundTripsInValueTargetsAndMatches()
    {
        var machine = Machine.Create("m")
            .Initial("a")
            .State("a", s => s.On<Go>(t => t.Target("foo\\.bar.inner")))
            .State("foo\\.bar", s => s.Initial("inner").State("inner"))
            .Build();

        var state = machine.Transition(machine.GetInitialState().State, new Go()).State;

        var value = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(state.Value);
        Assert.Equal("inner", value["foo\\.bar"]);
        Assert.True(state.Matches("foo\\.bar.inner"));
        Assert.True(state.Matches("#m.foo\\.bar.inner"));
        Assert.Equal(state.Configuration, machine.ConfigurationOf(state.Value));
    }

    [Fact]
    public void HashTarget_IdPlusPath_ThrowsWithSuggestion()
    {
        var error = Assert.Throws<ArgumentException>(() => Machine.Create("m")
            .Initial("a")
            .State("a", s => s.Id("top").Initial("b").State("b"))
            .State("c", s => s.On<Go>(t => t.Target("#top.b")))
            .Build());

        Assert.Equal("No state has id \"top.b\". Use \"#m.a.b\", or give that state an id.", error.Message);
    }

    [Fact]
    public void HashTarget_UnknownId_ThrowsAtCreation()
    {
        var error = Assert.Throws<ArgumentException>(() => Machine.Create("m")
            .Initial("a")
            .State("a", s => s.On<Go>(t => t.Target("#nope")))
            .Build());

        Assert.Equal("No state has id \"nope\".", error.Message);
    }

    [Fact]
    public void Matches_HashRequiresWholeId()
    {
        var machine = Machine.Create("m")
            .Initial("a")
            .State("a", s => s.Id("top").Initial("b").State("b"))
            .Build();

        var state = machine.GetInitialState().State;

        Assert.True(state.Matches("#top"));
        Assert.True(state.Matches("#m.a.b"));
        Assert.False(state.Matches("#top.b"));
        Assert.False(state.Matches("#m.a"));
    }

    [Fact]
    public void VersionWithoutId_Throws()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Machine.Create("")
            .Version("1.0.0")
            .Initial("a")
            .State("a")
            .Build());

        Assert.Equal("Machine with version '1.0.0' must declare an id.", error.Message);
    }
}
