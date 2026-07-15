using KoalaBooks.AppHostSupport;

namespace KoalaBooks.Tests;

public class VolumeNamingTests : IDisposable
{
    private readonly string _tempRoot;

    public VolumeNamingTests()
    {
        _tempRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "koalabooks-volumenaming-tests-" + Guid.NewGuid().ToString("N"))).FullName;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private string CreateCheckout(bool asMainCheckout, string appHostRelativeDir = "src/KoalaBooks.AppHost")
    {
        var repoRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"))).FullName;
        var appHostDir = Directory.CreateDirectory(Path.Combine(repoRoot, appHostRelativeDir)).FullName;
        var gitPath = Path.Combine(repoRoot, ".git");

        if (asMainCheckout)
        {
            Directory.CreateDirectory(gitPath); // real .git dir => main checkout
        }
        else
        {
            File.WriteAllText(gitPath, "gitdir: /some/main/repo/.git/worktrees/example\n"); // .git file => linked worktree
        }

        return appHostDir;
    }

    [Fact]
    public void IsMainCheckout_RealGitDirectory_ReturnsTrue()
    {
        var appHostDir = CreateCheckout(asMainCheckout: true);

        Assert.True(VolumeNaming.IsMainCheckout(appHostDir));
    }

    [Fact]
    public void IsMainCheckout_GitFilePointer_ReturnsFalse()
    {
        var appHostDir = CreateCheckout(asMainCheckout: false);

        Assert.False(VolumeNaming.IsMainCheckout(appHostDir));
    }

    [Fact]
    public void IsMainCheckout_NoGitMarkerAnywhere_DefaultsToTrue()
    {
        var noRepoDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "no-repo-here")).FullName;

        Assert.True(VolumeNaming.IsMainCheckout(noRepoDir));
    }

    [Fact]
    public void IsMainCheckout_GitMarkerInAncestorDirectory_IsFound()
    {
        var appHostDir = CreateCheckout(asMainCheckout: true, appHostRelativeDir: "src/nested/deeper/KoalaBooks.AppHost");

        Assert.True(VolumeNaming.IsMainCheckout(appHostDir));
    }

    [Fact]
    public void GetVolumeName_MainCheckout_ReturnsUnscopedBaseName()
    {
        var appHostDir = CreateCheckout(asMainCheckout: true);

        var result = VolumeNaming.GetVolumeName(overrideSuffix: null, sourcePath: Path.Combine(appHostDir, "AppHost.cs"));

        Assert.Equal(VolumeNaming.BaseName, result);
    }

    [Fact]
    public void GetVolumeName_Worktree_ReturnsHashSuffixedName()
    {
        var appHostDir = CreateCheckout(asMainCheckout: false);

        var result = VolumeNaming.GetVolumeName(overrideSuffix: null, sourcePath: Path.Combine(appHostDir, "AppHost.cs"));

        Assert.StartsWith(VolumeNaming.BaseName + "-", result);
        Assert.Matches("^" + System.Text.RegularExpressions.Regex.Escape(VolumeNaming.BaseName) + "-[0-9a-f]{8}$", result);
    }

    [Fact]
    public void GetVolumeName_WorktreeCalledTwice_IsDeterministic()
    {
        var appHostDir = CreateCheckout(asMainCheckout: false);
        var sourcePath = Path.Combine(appHostDir, "AppHost.cs");

        var first = VolumeNaming.GetVolumeName(overrideSuffix: null, sourcePath: sourcePath);
        var second = VolumeNaming.GetVolumeName(overrideSuffix: null, sourcePath: sourcePath);

        Assert.Equal(first, second);
    }

    [Fact]
    public void GetVolumeName_TwoDifferentWorktrees_ProduceDifferentSuffixes()
    {
        var appHostDirA = CreateCheckout(asMainCheckout: false);
        var appHostDirB = CreateCheckout(asMainCheckout: false);

        var nameA = VolumeNaming.GetVolumeName(overrideSuffix: null, sourcePath: Path.Combine(appHostDirA, "AppHost.cs"));
        var nameB = VolumeNaming.GetVolumeName(overrideSuffix: null, sourcePath: Path.Combine(appHostDirB, "AppHost.cs"));

        Assert.NotEqual(nameA, nameB);
    }

    [Fact]
    public void GetVolumeName_OverrideSuffix_TakesPrecedenceOverMainCheckout()
    {
        var appHostDir = CreateCheckout(asMainCheckout: true);

        var result = VolumeNaming.GetVolumeName(overrideSuffix: "scratch", sourcePath: Path.Combine(appHostDir, "AppHost.cs"));

        Assert.Equal(VolumeNaming.BaseName + "-scratch", result);
    }

    [Fact]
    public void GetVolumeName_OverrideSuffix_IsTrimmed()
    {
        var result = VolumeNaming.GetVolumeName(overrideSuffix: "  scratch  ", sourcePath: "/irrelevant/AppHost.cs");

        Assert.Equal(VolumeNaming.BaseName + "-scratch", result);
    }

    [Fact]
    public void GetVolumeName_WhitespaceOnlyOverrideSuffix_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            VolumeNaming.GetVolumeName(overrideSuffix: "   ", sourcePath: "/irrelevant/AppHost.cs"));
    }

    [Theory]
    [InlineData("valid-suffix")]
    [InlineData("valid_suffix")]
    [InlineData("valid.suffix")]
    [InlineData("a")]
    [InlineData("123")]
    public void ValidateSuffix_ValidValues_DoNotThrow(string suffix)
    {
        var exception = Record.Exception(() => VolumeNaming.ValidateSuffix(suffix, VolumeNaming.BaseName.Length));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("-leading-hyphen")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("has$dollar")]
    public void ValidateSuffix_InvalidCharacters_Throws(string suffix)
    {
        Assert.Throws<InvalidOperationException>(() => VolumeNaming.ValidateSuffix(suffix, VolumeNaming.BaseName.Length));
    }

    [Fact]
    public void ValidateSuffix_ExactlyAtDockerLengthLimit_DoesNotThrow()
    {
        // 255 - baseName.Length - 1 (separator) = max allowed suffix length
        var suffix = new string('a', 255 - VolumeNaming.BaseName.Length - 1);

        var exception = Record.Exception(() => VolumeNaming.ValidateSuffix(suffix, VolumeNaming.BaseName.Length));

        Assert.Null(exception);
    }

    [Fact]
    public void ValidateSuffix_OneOverDockerLengthLimit_Throws()
    {
        var suffix = new string('a', 255 - VolumeNaming.BaseName.Length); // one char too long

        Assert.Throws<InvalidOperationException>(() => VolumeNaming.ValidateSuffix(suffix, VolumeNaming.BaseName.Length));
    }
}
