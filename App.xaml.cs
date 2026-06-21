using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using LauncherV2.Core;
using LauncherV2.Plugins.DiabloII;
using LauncherV2.Plugins.OpenTTD;
using LauncherV2.Plugins.SoH;
using LauncherV2.UI.Pages;
// (WPF/WinForms disambiguation handled in GlobalUsings.cs)

namespace LauncherV2;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── Global crash capture ─────────────────────────────────────────────
        // Three nets: UI-thread exceptions (recoverable — handled + logged),
        // non-UI thread exceptions (process is dying — log before it goes),
        // and unobserved Task faults (logged, marked observed so they don't
        // escalate). Everything lands in crash.log next to the exe.
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("UI thread", args.Exception);
            try
            {
                System.Windows.Clipboard.SetText(args.Exception.ToString());
            }
            catch { /* clipboard can be locked by another process */ }
            MessageBox.Show(
                "An unexpected error occurred.\n\n" +
                $"{args.Exception.GetType().Name}: {args.Exception.Message}\n\n" +
                "Details were written to crash.log next to the launcher and " +
                "copied to your clipboard — please paste them in the Discord " +
                "bug-reports channel.",
                "Multiworld Launcher — Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;   // keep the launcher alive when possible
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                WriteCrashLog("background thread (fatal)", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("unobserved task", args.Exception);
            args.SetObserved();
        };

        var settings = SettingsStore.Load();

        // ── Register all game plugins ────────────────────────────────────────
        // V2.0.0: compiled-in. Future: scan a Plugins/ directory.

        GameRegistry.Register(new D2Plugin
        {
            // The mod installs into Games/diablo2_archipelago next to the launcher
            // — never the user's own Diablo II. DiabloIIPath now records where the
            // player's ORIGINAL Diablo II lives, used only to copy the MPQ data in.
            GameDirectory       = SettingsStore.DefaultGamePath("diablo2_archipelago"),
            OriginalD2Directory = settings.DiabloIIPath,
        });

        GameRegistry.Register(new OpenTTDPlugin
        {
            // Defaults to Games/OpenTTD next to the launcher exe.
            // User can override via settings.
        });

        // ── Native PC port with its own built-in AP client (ConnectsItself) ──
        GameRegistry.Register(new SoHPlugin
        {
            // Defaults to Games/ShipOfHarkinian next to the launcher exe.
            // User can override via settings.
        });

        // ── Emulator-based games (BizHawk + Lua AP bridge) ───────────────────
        GameRegistry.Register(new Plugins.Emulated.Games.PokemonEmeraldPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.ALinkToThePastPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.SuperMetroidPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.CastlevaniaCotMPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.PokemonRBPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.FinalFantasy1Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.SuperMarioWorldPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.EarthBoundPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.KirbysDreamLand3Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.MarioLuigiSuperstarSagaPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.YoshisIslandPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.MegaMan2Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.SMZ3Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.FinalFantasyMysticQuestPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.LufiaIIPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.ZillionPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.Castlevania64Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.MegaMan3Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.YuGiOh2006Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.SuperMarioLand2Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.LinksAwakeningDXPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.MetroidZeroMissionPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.MinishCapPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.WarioLand4Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.WarioLand3Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.MegaManXPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.LegendOfZeldaPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.GoldenSunTLAPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.PokemonCrystalPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.DonkeyKongCountryPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.MegaManX2Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.MegaManX3Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.PokemonFRLGPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.OracleOfSeasonsPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.DonkeyKongCountry2Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.OracleOfAgesPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.ZeldaIIPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.SymphonyOfTheNightPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.PokemonPlatinumPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.FinalFantasyVIPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.AdventurePlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.SonicTheHedgehogPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.SecretOfEvermorePlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.ChronoTriggerPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.BanjoTooiePlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.KingdomHeartsCoMPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.FinalFantasyIVPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.MarioKart64Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.DonkeyKong64Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.DiddyKongRacingPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.PaperMarioPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.Kirby64Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.PokemonBWPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.FireEmblem8Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.MetroidFusionPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.OcarinaOfTimePlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.SuperMarioRPGPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.FFTacticsAdvancePlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.MegaManBattleNetwork3Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.LandstalkerPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.DonkeyKongCountry3Plugin());

        // ── Native ConnectsItself games (own built-in AP client, no bridge) ───
        GameRegistry.Register(new Plugins.Doom.Doom1993Plugin());
        GameRegistry.Register(new Plugins.DoomII.DoomIIPlugin());
        GameRegistry.Register(new Plugins.Heretic.HereticPlugin());
        GameRegistry.Register(new Plugins.Jak.JakAndDaxterPlugin());
        GameRegistry.Register(new Plugins.Mario64.SuperMario64Plugin());
        GameRegistry.Register(new Plugins.Celeste64.Celeste64Plugin());
        GameRegistry.Register(new Plugins.BumperStickers.BumperStickersPlugin());
        GameRegistry.Register(new Plugins.ChecksFinder.ChecksFinderPlugin());
        GameRegistry.Register(new Plugins.SavingPrincess.SavingPrincessPlugin());
        GameRegistry.Register(new Plugins.HollowKnight.HollowKnightPlugin());
        GameRegistry.Register(new Plugins.StardewValley.StardewValleyPlugin());
        GameRegistry.Register(new Plugins.Tunic.TunicPlugin());
        GameRegistry.Register(new Plugins.RiskOfRain2.RiskOfRain2Plugin());
        GameRegistry.Register(new Plugins.Subnautica.SubnauticaPlugin());
        GameRegistry.Register(new Plugins.Undertale.UndertalePlugin());
        GameRegistry.Register(new Plugins.Meritous.MeritousPlugin());
        GameRegistry.Register(new Plugins.Terraria.TerrariaPlugin());
        GameRegistry.Register(new Plugins.Inscryption.InscryptionPlugin());
        GameRegistry.Register(new Plugins.Messenger.MessengerPlugin());
        GameRegistry.Register(new Plugins.Noita.NoitaPlugin());
        GameRegistry.Register(new Plugins.Aquaria.AquariaPlugin());
        GameRegistry.Register(new Plugins.AHatInTime.AHatInTimePlugin());
        GameRegistry.Register(new Plugins.Blasphemous.BlasphemousPlugin());
        GameRegistry.Register(new Plugins.Timespinner.TimespinnerPlugin());
        GameRegistry.Register(new Plugins.MuseDash.MuseDashPlugin());
        GameRegistry.Register(new Plugins.BombRushCyberfunk.BombRushCyberfunkPlugin());
        GameRegistry.Register(new Plugins.Faxanadu.FaxanaduPlugin());
        GameRegistry.Register(new Plugins.AShortHike.AShortHikePlugin());
        GameRegistry.Register(new Plugins.VVVVVV.VVVVVVPlugin());
        GameRegistry.Register(new Plugins.Witness.WitnessPlugin());
        GameRegistry.Register(new Plugins.DLCQuest.DLCQuestPlugin());
        GameRegistry.Register(new Plugins.StarCraft2.StarCraft2Plugin());
        GameRegistry.Register(new Plugins.Factorio.FactorioPlugin());
        GameRegistry.Register(new Plugins.Raft.RaftPlugin());
        GameRegistry.Register(new Plugins.Lingo.LingoPlugin());
        GameRegistry.Register(new Plugins.Wargroove.WargroovePlugin());
        GameRegistry.Register(new Plugins.Hylics2.Hylics2Plugin());
        GameRegistry.Register(new Plugins.Overcooked2.Overcooked2Plugin());
        GameRegistry.Register(new Plugins.Shivers.ShiversPlugin());
        GameRegistry.Register(new Plugins.Shapez.ShapezPlugin());
        GameRegistry.Register(new Plugins.Satisfactory.SatisfactoryPlugin());
        GameRegistry.Register(new Plugins.KingdomHearts1.KingdomHearts1Plugin());
        GameRegistry.Register(new Plugins.KingdomHearts2.KingdomHearts2Plugin());
        GameRegistry.Register(new Plugins.SonicAdventure2Battle.SonicAdventure2BattlePlugin());
        GameRegistry.Register(new Plugins.DarkSouls3.DarkSouls3Plugin());
        GameRegistry.Register(new Plugins.Civ6.Civ6Plugin());
        GameRegistry.Register(new Plugins.CivilizationV.CivilizationVPlugin());
        GameRegistry.Register(new Plugins.OldSchoolRunescape.OldSchoolRunescapePlugin());
        GameRegistry.Register(new Plugins.WindWaker.WindWakerPlugin());
        GameRegistry.Register(new Plugins.ChooChooCharles.ChooChooCharlesPlugin());
        GameRegistry.Register(new Plugins.CelesteOpenWorld.CelesteOpenWorldPlugin());
        GameRegistry.Register(new Plugins.Paint.PaintPlugin());
        GameRegistry.Register(new Plugins.Balatro.BalatroPlugin());
        GameRegistry.Register(new Plugins.Brotato.BrotatoPlugin());
        GameRegistry.Register(new Plugins.ADanceOfFireAndIce.ADanceOfFireAndIcePlugin());
        GameRegistry.Register(new Plugins.AgainstTheStorm.AgainstTheStormPlugin());
        GameRegistry.Register(new Plugins.Astalon.AstalonPlugin());
        GameRegistry.Register(new Plugins.AxiomVerge.AxiomVergePlugin());
        GameRegistry.Register(new Plugins.AnimalWell.AnimalWellPlugin());
        GameRegistry.Register(new Plugins.BloonsTD6.BloonsTD6Plugin());
        GameRegistry.Register(new Plugins.AnotherCrabsTreasure.AnotherCrabsTreasurePlugin());
        GameRegistry.Register(new Plugins.Anodyne.AnodynePlugin());
        GameRegistry.Register(new Plugins.Autopelago.AutopelagoPlugin());
        GameRegistry.Register(new Plugins.ADifficultGameAboutClimbing.ADifficultGameAboutClimbingPlugin());
        GameRegistry.Register(new Plugins.AnUntitledStory.AnUntitledStoryPlugin());
        GameRegistry.Register(new Plugins.CaveStory.CaveStoryPlugin());
        GameRegistry.Register(new Plugins.CavernOfDreams.CavernOfDreamsPlugin());
        GameRegistry.Register(new Plugins.BuckshotRoulette.BuckshotRoulettePlugin());
        GameRegistry.Register(new Plugins.CrossCode.CrossCodePlugin());
        GameRegistry.Register(new Plugins.ChainedEchoes.ChainedEchoesPlugin());
        GameRegistry.Register(new Plugins.CobaltCore.CobaltCorePlugin());
        GameRegistry.Register(new Plugins.CrystalProject.CrystalProjectPlugin());
        GameRegistry.Register(new Plugins.Cuphead.CupheadPlugin());
        GameRegistry.Register(new Plugins.DarkSoulsRemastered.DarkSoulsRemasteredPlugin());
        GameRegistry.Register(new Plugins.DarkSoulsII.DarkSoulsIIPlugin());
        GameRegistry.Register(new Plugins.DeathsDoor.DeathsDoorPlugin());
        GameRegistry.Register(new Plugins.Deltarune.DeltarunePlugin());
        GameRegistry.Register(new Plugins.DevilMayCry3.DevilMayCry3Plugin());
        GameRegistry.Register(new Plugins.DiceyDungeons.DiceyDungeonsPlugin());
        GameRegistry.Register(new Plugins.DomeKeeper.DomeKeeperPlugin());
        GameRegistry.Register(new Plugins.DontStarveTogether.DontStarveTogetherPlugin());
        GameRegistry.Register(new Plugins.Dredge.DredgePlugin());
        GameRegistry.Register(new Plugins.DukeNukem3D.DukeNukem3DPlugin());
        GameRegistry.Register(new Plugins.DungeonClawler.DungeonClawlerPlugin());
        GameRegistry.Register(new Plugins.EnderLilies.EnderLiliesPlugin());
        GameRegistry.Register(new Plugins.EnterTheGungeon.EnterTheGungeonPlugin());
        GameRegistry.Register(new Plugins.Everhood2.Everhood2Plugin());
        GameRegistry.Register(new Plugins.FreedomPlanet2.FreedomPlanet2Plugin());
        GameRegistry.Register(new Plugins.GettingOverIt.GettingOverItPlugin());
        GameRegistry.Register(new Plugins.GrimDawn.GrimDawnPlugin());
        GameRegistry.Register(new Plugins.Hades.HadesPlugin());
        GameRegistry.Register(new Plugins.Hammerwatch.HammerwatchPlugin());
        GameRegistry.Register(new Plugins.HasteBrokenWorlds.HasteBrokenWorldsPlugin());
        GameRegistry.Register(new Plugins.HatsuneMikuDiva.HatsuneMikuDivaPlugin());
        GameRegistry.Register(new Plugins.HereComesNiko.HereComesNikoPlugin());
        GameRegistry.Register(new Plugins.HeroCore.HeroCorePlugin());
        GameRegistry.Register(new Plugins.HiFiRush.HiFiRushPlugin());
        GameRegistry.Register(new Plugins.HighRoller.HighRollerPlugin());
        GameRegistry.Register(new Plugins.Holo8.Holo8Plugin());
        GameRegistry.Register(new Plugins.HololiveTreasureMountain.HololiveTreasureMountainPlugin());
        GameRegistry.Register(new Plugins.Iji.IjiPlugin());
        GameRegistry.Register(new Plugins.IttleDew2.IttleDew2Plugin());
        GameRegistry.Register(new Plugins.LethalCompany.LethalCompanyPlugin());
        GameRegistry.Register(new Plugins.LilGatorGame.LilGatorGamePlugin());
        GameRegistry.Register(new Plugins.LittleWitchNobeta.LittleWitchNobetaPlugin());
        GameRegistry.Register(new Plugins.Lunacid.LunacidPlugin());
        GameRegistry.Register(new Plugins.Mindustry.MindustryPlugin());
        GameRegistry.Register(new Plugins.Minecraft.MinecraftPlugin());
        GameRegistry.Register(new Plugins.MinishootAdventures.MinishootAdventuresPlugin());
        GameRegistry.Register(new Plugins.Minit.MinitPlugin());
        GameRegistry.Register(new Plugins.MomodoraMonlitFarewell.MomodoraMonlitFarewellPlugin());
        GameRegistry.Register(new Plugins.MonsterSanctuary.MonsterSanctuaryPlugin());
        GameRegistry.Register(new Plugins.NineSols.NineSolsPlugin());
        GameRegistry.Register(new Plugins.Nodebuster.NodebusterPlugin());
        GameRegistry.Register(new Plugins.PowerwashSimulator.PowerwashSimulatorPlugin());
        GameRegistry.Register(new Plugins.Prodigal.ProdigalPlugin());
        GameRegistry.Register(new Plugins.Pseudoregalia.PseudoregaliaPlugin());
        GameRegistry.Register(new Plugins.Psychonauts.PsychonautsPlugin());
        GameRegistry.Register(new Plugins.REPO.REPOPlugin());
        GameRegistry.Register(new Plugins.RabiRibi.RabiRibiPlugin());
        GameRegistry.Register(new Plugins.RainWorld.RainWorldPlugin());
        GameRegistry.Register(new Plugins.Refunct.RefunctPlugin());
        GameRegistry.Register(new Plugins.Reventure.ReventurePlugin());
        GameRegistry.Register(new Plugins.RiftOfTheNecroDancer.RiftOfTheNecroDancerPlugin());
        GameRegistry.Register(new Plugins.RiftWizard.RiftWizardPlugin());
        GameRegistry.Register(new Plugins.RiskOfRain.RiskOfRainPlugin());
        GameRegistry.Register(new Plugins.RustedMoss.RustedMossPlugin());
        GameRegistry.Register(new Plugins.SentinelsOfTheMultiverse.SentinelsOfTheMultiversePlugin());
        GameRegistry.Register(new Plugins.SeveredSoul.SeveredSoulPlugin());
        GameRegistry.Register(new Plugins.SlayTheSpire.SlayTheSpirePlugin());
        GameRegistry.Register(new Plugins.SlimeRancher.SlimeRancherPlugin());
        GameRegistry.Register(new Plugins.SmushiComeHome.SmushiComeHomePlugin());
        GameRegistry.Register(new Plugins.SonicAdventureDX.SonicAdventureDXPlugin());
        GameRegistry.Register(new Plugins.SonicHeroes.SonicHeroesPlugin());
        GameRegistry.Register(new Plugins.ShadowHedgehog.ShadowHedgehogPlugin());
        GameRegistry.Register(new Plugins.Spelunky2.Spelunky2Plugin());
        GameRegistry.Register(new Plugins.Stacklands.StacklandsPlugin());
        GameRegistry.Register(new Plugins.BindingOfIsaacRepentance.BindingOfIsaacRepentancePlugin());
        GameRegistry.Register(new Plugins.Webfishing.WebfishingPlugin());
        GameRegistry.Register(new Plugins.OuterWilds.OuterWildsPlugin());
        GameRegistry.Register(new Plugins.YokusIslandExpress.YokusIslandExpressPlugin());
        GameRegistry.Register(new Plugins.VoidStranger.VoidStrangerPlugin());
        GameRegistry.Register(new Plugins.Celeste.CelestePlugin());
        GameRegistry.Register(new Plugins.PizzaTower.PizzaTowerPlugin());
        GameRegistry.Register(new Plugins.OriAndTheWillOfTheWisps.OriAndTheWillOfTheWispsPlugin());
        GameRegistry.Register(new Plugins.Blasphemous2.Blasphemous2Plugin());
        GameRegistry.Register(new Plugins.YookaLaylee.YookaLayleePlugin());
        GameRegistry.Register(new Plugins.SpyroYearOfTheDragonPSX.SpyroYearOfTheDragonPSXPlugin());
        GameRegistry.Register(new Plugins.TurnipBoy.TurnipBoyPlugin());
        GameRegistry.Register(new Plugins.OriBlindForest.OriBlindForestPlugin());
        GameRegistry.Register(new Plugins.CryptOfTheNecroDancer.CryptOfTheNecroDancerPlugin());
        GameRegistry.Register(new Plugins.OxygenNotIncluded.OxygenNotIncludedPlugin());
        GameRegistry.Register(new Plugins.MetroidPrime.MetroidPrimePlugin());
        GameRegistry.Register(new Plugins.IntoTheBreach.IntoTheBreachPlugin());
        GameRegistry.Register(new Plugins.SlyCooper.SlyCooperPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.CastlevaniaHoDPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.CastlevaniaDoSPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.StarFox64Plugin());
        GameRegistry.Register(new Plugins.Sims4.Sims4Plugin());
        GameRegistry.Register(new Plugins.PokemonMysteryDungeonEoS.PokemonMysteryDungeonEoSPlugin());
        GameRegistry.Register(new Plugins.UFO50.UFO50Plugin());
        GameRegistry.Register(new Plugins.TyTheTasmanianTiger.TyTheTasmanianTigerPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.KirbySuperStarPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.KirbyAirRidePlugin());
        GameRegistry.Register(new Plugins.AgeOfEmpires2DE.AgeOfEmpires2DEPlugin());
        GameRegistry.Register(new Plugins.TotalWarWarhammer3.TotalWarWarhammer3Plugin());
        GameRegistry.Register(new Plugins.FinalFantasyXIITrialMode.FinalFantasyXIITrialModePlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.SuperMarioSunshinePlugin());
        GameRegistry.Register(new Plugins.DeepRockGalactic.DeepRockGalacticPlugin());
        GameRegistry.Register(new Plugins.SWE1R.SWE1RPlugin());
        GameRegistry.Register(new Plugins.SystemShock2.SystemShock2Plugin());
        GameRegistry.Register(new Plugins.PlateUp.PlateUpPlugin());
        GameRegistry.Register(new Plugins.TwilightPrincess.TwilightPrincessPlugin());
        GameRegistry.Register(new Plugins.SkywardSword.SkywardSwordPlugin());
        GameRegistry.Register(new Plugins.Zork.ZorkPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.ActRaiserPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.ApeEscapePlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.BraveFencerMusashiPlugin());
        GameRegistry.Register(new Plugins.CatQuest.CatQuestPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.CrystalisPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.GauntletLegendsPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.Spyro2RiptosRagePlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.LuigisMansionPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.ScoobyDooNightOf100FrightsPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.ToeJamAndEarlPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.StreetsOfRagePlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.SoulBlazerPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.MarioKartDoubleDashPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.SpongeBobBFBBPlugin());
        GameRegistry.Register(new Plugins.Wargroove2.Wargroove2Plugin());
        GameRegistry.Register(new Plugins.ZeldaMajorasMask.ZeldaMajorasMaskPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.ZeldaALinkBetweenWorldsPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.MediEvilPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.DigimonWorldPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.ArmoredCorePlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.Sly2BandOfThievesPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.TheGrinchPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.DragonWarriorPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.MarioIsMissingPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.PlokPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.SuperJunkoidPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.PokemonPinballPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.PaperMarioTTYDPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.Pikmin2Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.SimpsonsHitAndRunPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.SonicRidersPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.PokeParkWiiPlugin());
        GameRegistry.Register(new Plugins.WestOfLoathing.WestOfLoathingPlugin());
        GameRegistry.Register(new Plugins.Xcom2.Xcom2Plugin());
        GameRegistry.Register(new Plugins.HexcellsInfinite.HexcellsInfinitePlugin());
        GameRegistry.Register(new Plugins.TcgCardShopSimulator.TcgCardShopSimulatorPlugin());
        GameRegistry.Register(new Plugins.Tevi.TeviPlugin());
        GameRegistry.Register(new Plugins.ChecksMate.ChecksMatePlugin());
        GameRegistry.Register(new Plugins.ClusterTruck.ClusterTruckPlugin());
        GameRegistry.Register(new Plugins.DoronkoWanko.DoronkoWankoPlugin());
        GameRegistry.Register(new Plugins.Dracomino.DracominoPlugin());
        GameRegistry.Register(new Plugins.Frogmonster.FrogmonsterPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.ZeldaPhantomHourglassPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.ZeldaSpiritTracksPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.Shadowgate64Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.VoltorbFlipPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.FinalFantasyTacticsA2Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.ApeEscape3Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.KOnPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.RatchetAndClankPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.YuGiOhForbiddenMemoriesPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.SonicRushPlugin());
        GameRegistry.Register(new Plugins.CornKidz64.CornKidz64Plugin());
        GameRegistry.Register(new Plugins.FridayNightFunkin.FridayNightFunkinPlugin());
        GameRegistry.Register(new Plugins.Glyphs.GlyphsPlugin());
        GameRegistry.Register(new Plugins.IslesOfSeaAndSky.IslesOfSeaAndSkyPlugin());
        GameRegistry.Register(new Plugins.OurAscent.OurAscentPlugin());
        GameRegistry.Register(new Plugins.GuildWars2.GuildWars2Plugin());
        GameRegistry.Register(new Plugins.GzDoom.GzDoomPlugin());
        GameRegistry.Register(new Plugins.KeepTalking.KeepTalkingPlugin());
        GameRegistry.Register(new Plugins.LegoBatman.LegoBatmanPlugin());
        GameRegistry.Register(new Plugins.LegoStarWars.LegoStarWarsPlugin());
        GameRegistry.Register(new Plugins.Trackmania.TrackmaniaPlugin());
        GameRegistry.Register(new Plugins.Tyrian.TyrianPlugin());
        GameRegistry.Register(new Plugins.Osu.OsuPlugin());
        GameRegistry.Register(new Plugins.Parkitect.ParkitectPlugin());
        GameRegistry.Register(new Plugins.Shapez2.Shapez2Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.XenobladeChroniclesXPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.RatchetAndClankGoingCommandoPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.RatchetAndClankUpYourArsenalPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.YuGiOhDungeonDiceMonstersPlugin());
        GameRegistry.Register(new Plugins.KingdomHeartsBBS.KingdomHeartsBBSPlugin());
        GameRegistry.Register(new Plugins.KeymastersKeep.KeymastersKeepPlugin());
        GameRegistry.Register(new Plugins.StickRanger.StickRangerPlugin());
        GameRegistry.Register(new Plugins.UnfairFlips.UnfairFlipsPlugin());
        GameRegistry.Register(new Plugins.YARG.YARGPlugin());
        GameRegistry.Register(new Plugins.FF1PixelRemaster.FF1PixelRemasterPlugin());
        GameRegistry.Register(new Plugins.MinecraftDig.MinecraftDigPlugin());
        GameRegistry.Register(new Plugins.OpenRCT2.OpenRCT2Plugin());
        GameRegistry.Register(new Plugins.PinballFX3.PinballFX3Plugin());
        GameRegistry.Register(new Plugins.PlacidPlasticDuckSimulator.PlacidPlasticDuckSimulatorPlugin());
        GameRegistry.Register(new Plugins.Rayman2.Rayman2Plugin());
        GameRegistry.Register(new Plugins.SuperCatPlanet.SuperCatPlanetPlugin());
        GameRegistry.Register(new Plugins.TABS.TABSPlugin());
        GameRegistry.Register(new Plugins.DeliveringHOPE.DeliveringHOPEPlugin());
        GameRegistry.Register(new Plugins.Lingo2.Lingo2Plugin());
        GameRegistry.Register(new Plugins.VoidSols.VoidSolsPlugin());
        GameRegistry.Register(new Plugins.TrailsInTheSkyThe3rd.TrailsInTheSkyThe3rdPlugin());
        GameRegistry.Register(new Plugins.FFXII.FFXIIPlugin());
        GameRegistry.Register(new Plugins.CandyBox2.CandyBox2Plugin());
        GameRegistry.Register(new Plugins.CrosswordAP.CrosswordAPPlugin());
        GameRegistry.Register(new Plugins.GarfieldKart.GarfieldKartPlugin());
        GameRegistry.Register(new Plugins.LeagueOfLegends.LeagueOfLegendsPlugin());
        GameRegistry.Register(new Plugins.Loonyland.LoonylandPlugin());
        GameRegistry.Register(new Plugins.RiskOfRainReturns.RiskOfRainReturnsPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.BombermanQuestPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.Bomberman64Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.Bomberman64SecondAttackPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.SuperSmashBrosMeleePlugin());
        GameRegistry.Register(new Plugins.Touhou185.Touhou185Plugin());
        GameRegistry.Register(new Plugins.TwistyCube.TwistyCubePlugin());
        GameRegistry.Register(new Plugins.Wordipelago.WordipelagoPlugin());
        GameRegistry.Register(new Plugins.Spinball.SpinballPlugin());
        GameRegistry.Register(new Plugins.SimonTathamPuzzles.SimonTathamPuzzlesPlugin());
        GameRegistry.Register(new Plugins.TheForgedCurse.TheForgedCursePlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.PanelDePonPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.MarioSuperSluggersPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.SuperMarioSunshineArcade2Plugin());
        GameRegistry.Register(new Plugins.Emulated.Games.CastlevaniaLegacyOfDarknessPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.SpicyMycenaWafflesPlugin());
        GameRegistry.Register(new Plugins.Jigsaw.JigsawPlugin());
        GameRegistry.Register(new Plugins.WateryWords.WateryWordsPlugin());
        GameRegistry.Register(new Plugins.Clique.CliquePlugin());
        GameRegistry.Register(new Plugins.AirDelivery.AirDeliveryPlugin());
        GameRegistry.Register(new Plugins.MetroCUBEvania.MetroCUBEvaniaPlugin());
        GameRegistry.Register(new Plugins.Archipeladoku.ArchipeladokuPlugin());
        GameRegistry.Register(new Plugins.BKPicross.BKPicrossPlugin());
        GameRegistry.Register(new Plugins.NonogramAP.NonogramAPPlugin());
        GameRegistry.Register(new Plugins.Emulated.Games.BlueSpherePlugin());
        GameRegistry.Register(new Plugins.YachtDice.YachtDicePlugin());
        GameRegistry.Register(new Plugins.RogueLegacy2.RogueLegacy2Plugin());
        GameRegistry.Register(new Plugins.JetIsland.JetIslandPlugin());
        GameRegistry.Register(new Plugins.BabaIsYou.BabaIsYouPlugin());
        GameRegistry.Register(new Plugins.Ultrakill.UltrakillPlugin());
        GameRegistry.Register(new Plugins.NeonWhite.NeonWhitePlugin());
        GameRegistry.Register(new Plugins.SwRacer.SwRacerPlugin());
        GameRegistry.Register(new Plugins.Apquest.ApquestPlugin());
        GameRegistry.Register(new Plugins.APSudoku.APSudokuPlugin());
        GameRegistry.Register(new Plugins.ArchipelaGo.ArchipelaGoPlugin());
        GameRegistry.Register(new Plugins.HintMachine.HintMachinePlugin());
        GameRegistry.Register(new Plugins.AM2R.AM2RPlugin());
        GameRegistry.Register(new Plugins.RE2R.RE2RPlugin());
        GameRegistry.Register(new Plugins.RE3R.RE3RPlugin());
        GameRegistry.Register(new Plugins.Hacknet.HacknetPlugin());
        GameRegistry.Register(new Plugins.CavesOfQud.CavesOfQudPlugin());

        GameRegistry.Register(new Plugins.VampireSurvivors.VampireSurvivorsPlugin());
        GameRegistry.Register(new Plugins.ClairObscur.ClairObscurPlugin());
        GameRegistry.Register(new Plugins.Unbeatable.UnbeatablePlugin());
        GameRegistry.Register(new Plugins.Hades2.Hades2Plugin());

        GameRegistry.Register(new Plugins.FlipWitch.FlipWitchPlugin());
        GameRegistry.Register(new Plugins.Signalis.SignalisPlugin());
        GameRegistry.Register(new Plugins.Peak.PeakPlugin());
        GameRegistry.Register(new Plugins.OblivionRemastered.OblivionRemasteredPlugin());










        // ── Splash screen ────────────────────────────────────────────────────
        // Show splash, then reveal the main window after a short minimum delay.
        var splash = new SplashWindow();
        splash.Show();

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;

        // Hide main window initially so it doesn't flash under the splash.
        mainWindow.Visibility = Visibility.Hidden;

        // Show for at least 700 ms, then cross-fade into the main window.
        _ = ShowMainWindowAsync(mainWindow, splash);
    }

    private static async Task ShowMainWindowAsync(MainWindow mainWindow, SplashWindow splash)
    {
        // Run the launcher self-update check during the splash dwell time.
        // If a new version is available we download and apply it right here —
        // the user sees "Updating… N%" on the splash, then the batch restarts
        // the launcher automatically. The main window never opens.
        var updater = new LauncherUpdater();
        bool updateApplied = false;
        try
        {
            bool updateFound = await updater.CheckAsync();
            if (updateFound)
            {
                splash.SetUpdateStatus($"Updating to v{updater.LatestVersion}…");
                updater.DownloadProgress += pct =>
                    splash.SetUpdateStatus($"Updating to v{updater.LatestVersion}…  {pct}%");
                await updater.DownloadAndApplyAsync();
                updateApplied = true;   // App.Shutdown() called inside DownloadAndApplyAsync
            }
        }
        catch
        {
            // Silent: network or SHA mismatch → fall through and open the main window.
            splash.SetUpdateStatus("");
        }

        if (updateApplied) return;   // batch script will restart us; nothing else to do

        // Enforce a minimum 700 ms dwell so the splash doesn't flash for fast startups.
        await Task.Delay(700);
        await splash.FadeOutAsync();   // 300 ms fade-out
        mainWindow.Visibility = Visibility.Visible;
        mainWindow.Activate();
    }

    // ── Crash log ────────────────────────────────────────────────────────────

    private static readonly object _crashLogLock = new();

    /// <summary>
    /// Append an exception to crash.log next to the exe. Never throws.
    /// Rotates through crash_0.log / crash_1.log / crash_2.log at 1 MB each,
    /// keeping up to 3 MB of crash history instead of discarding on overflow.
    /// </summary>
    internal static void WriteCrashLog(string source, Exception ex)
    {
        try
        {
            string dir  = AppContext.BaseDirectory;
            string path = Path.Combine(dir, "crash.log");
            lock (_crashLogLock)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 1_048_576)
                {
                    // Rotate: crash_1.log → crash_2.log, crash_0.log → crash_1.log,
                    // crash.log → crash_0.log. Silently drop crash_2 if it exists.
                    string slot2 = Path.Combine(dir, "crash_2.log");
                    string slot1 = Path.Combine(dir, "crash_1.log");
                    string slot0 = Path.Combine(dir, "crash_0.log");
                    if (File.Exists(slot2)) File.Delete(slot2);
                    if (File.Exists(slot1)) File.Move(slot1, slot2);
                    if (File.Exists(slot0)) File.Move(slot0, slot1);
                    File.Move(path, slot0);
                }
                File.AppendAllText(path,
                    $"════════ {DateTime.Now:yyyy-MM-dd HH:mm:ss} · " +
                    $"v{LauncherUpdater.CurrentVersion} · {source} ════════\r\n" +
                    $"{ex}\r\n\r\n");
            }
        }
        catch { /* a crash logger must never crash */ }
    }
}

