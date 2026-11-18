using System.Collections.Generic;

namespace clonengo
{
    /// <summary>Ryhmä profiilin sisällä olevia polkuja, jotka voidaan jättää kopioimatta.</summary>
    public sealed class ExclusionOption
    {
        public string Label { get; set; } = "";
        public string Description { get; set; } = "";
        /// <summary>Profiilikansioon suhteelliset polut; * toimii jokerimerkkinä yhdelle kansiotasolle.</summary>
        public string[] Paths { get; set; } = System.Array.Empty<string>();
        public bool IsExcluded { get; set; } = true;

        public static List<ExclusionOption> CreateDefaults() => new()
        {
            new() { Label = "Väliaikaiset tiedostot", Description = @"AppData\Local\Temp, CrashDumps",
                    Paths = new[] { @"AppData\Local\Temp", @"AppData\Local\CrashDumps" } },
            new() { Label = "Selainten välimuistit", Description = "Edge, Chrome ja Firefox: Cache, Code Cache, GPUCache, cache2 (kirjanmerkit ja salasanat säilyvät)",
                    Paths = new[] {
                        @"AppData\Local\Microsoft\Edge\User Data\*\Cache", @"AppData\Local\Microsoft\Edge\User Data\*\Code Cache", @"AppData\Local\Microsoft\Edge\User Data\*\GPUCache",
                        @"AppData\Local\Google\Chrome\User Data\*\Cache", @"AppData\Local\Google\Chrome\User Data\*\Code Cache", @"AppData\Local\Google\Chrome\User Data\*\GPUCache",
                        @"AppData\Local\Mozilla\Firefox\Profiles\*\cache2" } },
            new() { Label = "Windowsin välimuistit", Description = "INetCache, WebCache, Explorer (pikkukuvat)",
                    Paths = new[] { @"AppData\Local\Microsoft\Windows\INetCache", @"AppData\Local\Microsoft\Windows\WebCache", @"AppData\Local\Microsoft\Windows\Explorer" } },
            new() { Label = "Store-sovellusten välimuistit", Description = @"Packages\*\TempState, AC, LocalCache",
                    Paths = new[] { @"AppData\Local\Packages\*\TempState", @"AppData\Local\Packages\*\AC", @"AppData\Local\Packages\*\LocalCache" } },
            new() { Label = "Viestintäsovellusten välimuistit", Description = "Teams, Slack, Discord, Spotify (kirjautuminen voi pyytää uudelleen)",
                    Paths = new[] { @"AppData\Roaming\Microsoft\Teams", @"AppData\Roaming\Slack\Cache", @"AppData\Roaming\discord\Cache", @"AppData\Local\Spotify\Storage" } },
            new() { Label = "OneDriven asetukset", Description = @"AppData\Local\Microsoft\OneDrive (pilvitiedostot eivät ole tässä)",
                    Paths = new[] { @"AppData\Local\Microsoft\OneDrive" } },
            new() { Label = "Kehittäjätyökalujen välimuistit", Description = "pip, npm, NuGet, Gradle, Maven, .cache",
                    Paths = new[] { @"AppData\Local\pip\Cache", @"AppData\Roaming\npm-cache", @"AppData\Local\npm-cache", @".nuget\packages", @".gradle\caches", @".m2\repository", @".cache" } },
            new() { Label = "Visual Studion väliaikaiset", Description = "ComponentModelCache, Temp",
                    Paths = new[] { @"AppData\Local\Microsoft\VisualStudio\*\ComponentModelCache", @"AppData\Local\Microsoft\VisualStudio\*\Temp" } },
            new() { Label = "Projektien käännöstiedostot", Description = ".vs-, bin- ja obj-kansiot",
                    Paths = new[] { @"**\.vs", @"**\bin", @"**\obj" } },
        };
    }
}
