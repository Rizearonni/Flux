using System;
using System.Collections.Generic;
using Flux;

// Simple test runner for validating LuaRunner SavedVariables behavior without xUnit.
class Program
{
    static int Main(string[] args)
    {
        int failures = 0;
        try
        {
            TestSavedVariablesAssignmentTriggersEventAndPersists(ref failures);
            TestLoadingSavedVariablesPopulatesObject(ref failures);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Unhandled exception in tests: " + ex);
            return 2;
        }

        if (failures == 0)
        {
            Console.WriteLine("All tests passed.");
            return 0;
        }
        else
        {
            Console.WriteLine($"{failures} test(s) failed.");
            return 1;
        }
    }

    static void TestSavedVariablesAssignmentTriggersEventAndPersists(ref int failures)
    {
        var name = "TestAddon";
        var runner = new LuaRunner(name, null, null);

        bool eventFired = false;
        string? firedName = null;
        runner.OnSavedVariablesChanged += (s, n) => { eventFired = true; firedName = n; };

        runner.RunScriptFromString("SavedVariables.myval = 123", name);

        if (!eventFired) { Console.WriteLine("FAIL: OnSavedVariablesChanged did not fire."); failures++; }
        else if (firedName != name) { Console.WriteLine($"FAIL: event fired with wrong name: {firedName}"); failures++; }
        else
        {
            var obj = runner.GetSavedVariablesAsObject();
            if (!obj.ContainsKey("myval")) { Console.WriteLine("FAIL: SavedVariables missing key 'myval'."); failures++; }
            else if (!(obj["myval"] is double d && d == 123.0)) { Console.WriteLine($"FAIL: SavedVariables.myval wrong value: {obj["myval"]}"); failures++; }
            else Console.WriteLine("PASS: TestSavedVariablesAssignmentTriggersEventAndPersists");
        }
    }

    static void TestLoadingSavedVariablesPopulatesObject(ref int failures)
    {
        var name = "LoadTest";
        var runner = new LuaRunner(name, null, null);
        var initial = new Dictionary<string, object?> { { "foo", 5 }, { "bar", "baz" } };
        runner.LoadSavedVariables(initial);

        var obj = runner.GetSavedVariablesAsObject();
        bool ok = true;
        if (!(obj.ContainsKey("foo") && obj["foo"] is double df && df == 5.0)) { Console.WriteLine("FAIL: foo missing or wrong"); ok = false; }
        if (!(obj.ContainsKey("bar") && obj["bar"] as string == "baz")) { Console.WriteLine("FAIL: bar missing or wrong"); ok = false; }
        if (ok) Console.WriteLine("PASS: TestLoadingSavedVariablesPopulatesObject"); else failures++;
    }
}
