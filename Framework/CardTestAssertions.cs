using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using Xunit;

namespace TestTheSpire;

public static class CardTestAssertions
{
    public static int PowerAmount<TPower>(Creature creature)
        where TPower : PowerModel
    {
        return creature.GetPower<TPower>()?.Amount ?? 0;
    }

    public static void AssertPowerAmount<TPower>(Creature creature, int expected)
        where TPower : PowerModel
    {
        Assert.Equal(expected, PowerAmount<TPower>(creature));
    }

    public static void AssertCurrentPortraitPathsExist(CardModel card, string? projectDirectory = null)
    {
        Assert.NotEmpty(card.AllPortraitPaths);
        Assert.Contains(card.PortraitPath, card.AllPortraitPaths);
        Assert.True(
            ProjectFileExists(card.PortraitPath, projectDirectory),
            $"{card.PortraitPath} should exist.");
        Assert.All(
            card.AllPortraitPaths,
            path => Assert.True(
                ProjectFileExists(path, projectDirectory),
                $"{path} should exist."));
    }

    public static bool ProjectFileExists(string? resourcePath, string? projectDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(resourcePath)) return false;

        if (ResourceLoader.Exists(resourcePath)) return true;

        var relativePath = resourcePath.StartsWith("res://", StringComparison.Ordinal)
            ? resourcePath["res://".Length..]
            : resourcePath;

        if (Path.IsPathRooted(relativePath)) return File.Exists(relativePath);

        if (!string.IsNullOrWhiteSpace(projectDirectory))
            return File.Exists(Path.Combine(projectDirectory, relativePath));

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, relativePath))) return true;
        }

        return false;
    }
}
