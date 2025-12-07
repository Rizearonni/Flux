using System;
using System.Collections.Generic;
using Xunit;
using Flux;

namespace Flux.Tests
{
    public class LuaRunnerSavedVarsTests
    {
        [Fact]
        public void SavedVariablesAssignment_Triggers_OnSavedVariablesChanged_And_Persists()
        {
            var name = "TestAddon";
            var runner = new LuaRunner(name, null, null);

            bool eventFired = false;
            string? firedName = null;
            runner.OnSavedVariablesChanged += (s, n) => { eventFired = true; firedName = n; };

            // set a saved variable from Lua
            runner.RunScriptFromString("SavedVariables.myval = 123", name);

            Assert.True(eventFired, "OnSavedVariablesChanged should have fired when SavedVariables were written from Lua");
            Assert.Equal(name, firedName);

            var obj = runner.GetSavedVariablesAsObject();
            Assert.True(obj.ContainsKey("myval"));
            Assert.Equal(123.0, obj["myval"]); // MoonSharp stores numbers as double
        }

        [Fact]
        public void LoadingSavedVariables_Populates_GetSavedVariablesAsObject()
        {
            var name = "LoadTest";
            var runner = new LuaRunner(name, null, null);
            var initial = new Dictionary<string, object?> { { "foo", 5 }, { "bar", "baz" } };
            runner.LoadSavedVariables(initial);

            var obj = runner.GetSavedVariablesAsObject();
            Assert.Equal(5.0, obj["foo"]);
            Assert.Equal("baz", obj["bar"]);
        }
    }
}
