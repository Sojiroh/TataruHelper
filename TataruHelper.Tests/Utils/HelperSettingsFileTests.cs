using FFXIVTataruHelper;

using NUnit.Framework;

using System;
using System.Collections.Generic;
using System.IO;

namespace TataruHelper.Tests
{
    /// <summary>
    /// The two failure modes of the settings file: a save that dies halfway
    /// must not leave a truncated file, and a file that will not read must
    /// survive the attempt to load it - quarantined, not overwritten.
    /// </summary>
    public class HelperSettingsFileTests
    {
        private static string NewPath()
        {
            var directory = Path.Combine(
                Path.GetTempPath(), "tataru-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "settings.json");
        }

        private static void DeletePath(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                Directory.Delete(directory, true);
        }

        [Test]
        public void ACompletedSave_RoundTrips()
        {
            var path = NewPath();

            Helper.SaveJson(new List<string> { "one", "two" }, path);

            var reloaded = Helper.LoadJsonData<List<string>>(path);

            Assert.That(reloaded, Is.EqualTo(new List<string> { "one", "two" }));
            Assert.That(File.Exists(path + ".new"), Is.False, "the side file must be moved away");
        }

        [Test]
        public void ChatWindowTabState_SurvivesJsonRoundTrip()
        {
            var path = NewPath();
            try
            {
                var settings = new UserSettings();
                settings.ChatWindows.Add(new ChatWindowViewModelSettings("First", 17)
                {
                    TabGroupId = "11111111-1111-1111-1111-111111111111",
                    IsActiveTab = false
                });
                settings.ChatWindows.Add(new ChatWindowViewModelSettings("Second", 99)
                {
                    TabGroupId = "11111111-1111-1111-1111-111111111111",
                    IsActiveTab = true
                });

                Helper.SaveJson(settings, path);

                var reloaded = Helper.LoadJsonData<UserSettings>(path);

                Assert.Multiple(() =>
                {
                    Assert.That(reloaded.ChatWindows, Has.Count.EqualTo(2));
                    Assert.That(reloaded.ChatWindows[0].TabGroupId,
                        Is.EqualTo("11111111-1111-1111-1111-111111111111"));
                    Assert.That(reloaded.ChatWindows[0].IsActiveTab, Is.False);
                    Assert.That(reloaded.ChatWindows[1].TabGroupId,
                        Is.EqualTo("11111111-1111-1111-1111-111111111111"));
                    Assert.That(reloaded.ChatWindows[1].IsActiveTab, Is.True);
                });
            }
            finally
            {
                DeletePath(path);
            }
        }

        [Test]
        public void LegacyChatWindowJson_RemainsStandalone()
        {
            var path = NewPath();
            try
            {
                File.WriteAllText(path,
                    "{ \"ChatWindows\": [ { \"Name\": \"Legacy\", \"WinId\": 42 } ] }");

                var reloaded = Helper.LoadJsonData<UserSettings>(path);

                Assert.That(reloaded.ChatWindows, Has.Count.EqualTo(1));
                Assert.That(string.IsNullOrEmpty(reloaded.ChatWindows[0].TabGroupId), Is.True);
            }
            finally
            {
                DeletePath(path);
            }
        }

        [Test]
        public void AFileThatWillNotRead_IsKeptUntouchedAside()
        {
            var path = NewPath();
            var onDisk = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            File.WriteAllBytes(path, onDisk);

            Helper.LoadJsonData<List<string>>(path);

            Assert.That(File.Exists(path), Is.False, "the unreadable file must be moved aside");
            Assert.That(File.ReadAllBytes(path + ".corrupt"), Is.EqualTo(onDisk));
        }
    }
}