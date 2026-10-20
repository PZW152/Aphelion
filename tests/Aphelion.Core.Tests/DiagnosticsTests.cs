using Aphelion.Core.Diagnostics;

namespace Aphelion.Core.Tests;

public class CrashAnalyzerTests
{
    private static CrashAnalysis Analyze(string text, string name = "output") => CrashAnalyzer.Analyze([new CrashSource(name, text)], 1);

    [Fact]
    public void Out_of_memory()
    {
        var a = Analyze("Exception in thread \"main\" java.lang.OutOfMemoryError: Java heap space");
        Assert.Equal("crash.memory", a.Findings[0].Code);
    }

    [Fact]
    public void Class_version_maps_to_required_java()
    {
        var a = Analyze("java.lang.UnsupportedClassVersionError: net/minecraft/client/main/Main has been compiled by a more recent version of the Java Runtime (class file version 65.0), this version of the Java Runtime only recognizes class file versions up to 52.0");
        Assert.Equal("crash.javaTooOld", a.Findings[0].Code);
        Assert.Equal("21", a.Findings[0].Args["required"]);
    }

    [Fact]
    public void Old_launchwrapper_on_new_java()
    {
        var a = Analyze("java.lang.ClassCastException: class jdk.internal.loader.ClassLoaders$AppClassLoader cannot be cast to class java.net.URLClassLoader");
        Assert.Equal("crash.javaTooNew", a.Findings[0].Code);
    }

    [Fact]
    public void Forge_missing_dependencies_are_listed()
    {
        var a = Analyze("""
            Missing or unsupported mandatory dependencies:
            	Mod ID: 'geckolib', Requested by: 'alexsmobs', Expected range: '[4.4,)', Actual version: '[MISSING]'
            	Mod ID: 'forge', Requested by: 'create', Expected range: '[47.1.3,)', Actual version: '47.0.1'
            """);
        var f = a.Findings[0];
        Assert.Equal("crash.forgeDeps", f.Code);
        Assert.Contains("geckolib ← alexsmobs", f.Args["details"]);
        Assert.Contains("forge ← create", f.Args["details"]);
    }

    [Fact]
    public void Fabric_incompatible_set_details()
    {
        var a = Analyze("""
            net.fabricmc.loader.impl.FormattedException: Mod resolution encountered an incompatible mod set!
            A potential solution has been determined:
            	 - Install fabric-api, any version.
            	 - Replace mod 'Sodium' (sodium) 0.4.0 with any version that is compatible with minecraft 1.21.1.
            """);
        Assert.Equal("crash.fabricDeps", a.Findings[0].Code);
        Assert.Contains("Install fabric-api", a.Findings[0].Args["details"]);
    }

    [Fact]
    public void Mixin_failure_names_the_mod()
    {
        var a = Analyze("org.spongepowered.asm.mixin.transformer.throwables.MixinTransformerError: Mixin apply for mod examplemod failed examplemod.mixins.json:FooMixin");
        Assert.Equal("crash.mixin", a.Findings[0].Code);
        Assert.Equal("examplemod", a.Findings[0].Args["mod"]);
    }

    [Fact]
    public void Graphics_driver_problems()
    {
        Assert.Equal("crash.graphicsDriver", Analyze("org.lwjgl.LWJGLException: Pixel format not accelerated").Findings[0].Code);
        var hs = Analyze("# Problematic frame:\n# C  [atio6axx.dll+0x1a2b3c]", "hs_err");
        Assert.Equal("crash.driverNative", hs.Findings[0].Code);
        Assert.Equal("atio6axx.dll", hs.Findings[0].Args["dll"]);
    }

    [Fact]
    public void Crash_report_description_and_suspected_mods()
    {
        var a = CrashAnalyzer.Analyze([new CrashSource("crash-report", "---- Minecraft Crash Report ----\nDescription: Ticking entity\n\nSuspected Mods: Create (create)\n", @"C:\x\crash.txt")], 1);
        Assert.Equal("Ticking entity", a.Description);
        Assert.Equal(@"C:\x\crash.txt", a.CrashReportPath);
        Assert.Equal("crash.suspectedMods", a.Findings[0].Code);
        Assert.Equal("Create (create)", a.Findings[0].Args["mods"]);
    }

    [Fact]
    public void Unknown_crash_still_yields_a_finding_with_the_exit_code()
    {
        var a = CrashAnalyzer.Analyze([new CrashSource("output", "nothing recognisable")], -1073740791);
        Assert.Equal("crash.unknown", Assert.Single(a.Findings).Code);
        Assert.Equal("-1073740791", a.Findings[0].Args["code"]);
    }

    [Fact]
    public void Generic_missing_class_is_dropped_when_a_specific_cause_exists()
    {
        var a = Analyze("java.lang.NoClassDefFoundError: net/fabricmc/fabric/api/Foo\njava.lang.OutOfMemoryError");
        Assert.DoesNotContain(a.Findings, f => f.Code == "crash.missingClass");
    }
}
