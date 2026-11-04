using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.Source1;
using ValveResourceFormat.Source1.Bsp;
using ValveResourceFormat.Source1.FileSystem;
using ValveResourceFormat.Source1.Model;
using ValveResourceFormat.Source1.Texture;

namespace CLI
{
    public partial class Decompiler
    {
        private const uint VtfMagic = 0x00465456;
        private const uint StudioModelMagic = 0x54534449;
        private const uint BspMagic = 0x50534256;

        private sealed class Source1TestGame : IDisposable
        {
            public Source1FileSystem FileSystem { get; }
            public Source1ResourceProvider Provider { get; }
            public GameFileLoader FileLoader { get; }

            public Source1TestGame(Source1FileSystem fileSystem, ILogger logger)
            {
                FileSystem = fileSystem;
                Provider = new Source1ResourceProvider(fileSystem, logger);
                FileLoader = new GameFileLoader(null, null, logger)
                {
                    ResourceProvider = Provider,
                };
            }

            public void Dispose()
            {
                FileLoader.Dispose();
                FileSystem.Dispose();
            }
        }

        /// <summary>
        /// Games that textures, materials and models are converted in, shared by the files of each game.
        /// </summary>
        private readonly Dictionary<string, Source1TestGame?> Source1TestGames = new(StringComparer.OrdinalIgnoreCase);

        private bool IsSupportedFileName(string fileName)
            => SupportedFileNamesRegex().IsMatch(fileName) || (CollectStats && Source1FileNamesRegex().IsMatch(fileName));

        private Source1TestGame? CreateSource1TestGame(string diskPath)
        {
            var fileSystem = Source1FileSystem.FromPath(diskPath);

            if (fileSystem == null)
            {
                return null;
            }

            return new Source1TestGame(fileSystem, FileLoaderLogger);
        }

        private Source1TestGame? GetSharedSource1TestGame(string diskPath)
        {
            if (!StatsWithLoader)
            {
                return null;
            }

            var key = Source1FileSystem.FindGameInfo(diskPath) ?? Path.GetDirectoryName(diskPath) ?? diskPath;

            lock (Source1TestGames)
            {
                if (!Source1TestGames.TryGetValue(key, out var game))
                {
                    game = CreateSource1TestGame(diskPath);
                    Source1TestGames.Add(key, game);
                }

                return game;
            }
        }

        private void DisposeSource1TestGames()
        {
            lock (Source1TestGames)
            {
                foreach (var game in Source1TestGames.Values)
                {
                    game?.Dispose();
                }
            }
        }

        /// <summary>
        /// Converts a Source 1 texture, material, model or map to Source 2 resources and tests them like any other resource.
        /// </summary>
        /// <returns>False if the file is not a Source 1 file.</returns>
        private bool TryTestSource1File(string path, Stream stream, uint magic, string? originalPath, Package? package)
        {
            var isMaterial = Path.GetExtension(path).Equals(".vmt", StringComparison.OrdinalIgnoreCase);

            if (magic != VtfMagic && magic != StudioModelMagic && magic != BspMagic && !isMaterial)
            {
                return false;
            }

            try
            {
                var diskPath = package?.FileName ?? path;

                if (magic == VtfMagic)
                {
                    TestSource1Texture(path, stream, originalPath);
                }
                else if (magic == StudioModelMagic)
                {
                    TestSource1Model(path, stream, originalPath, package, GetSharedSource1TestGame(diskPath));
                }
                else if (magic == BspMagic)
                {
                    TestSource1Map(path, stream, originalPath, diskPath);
                }
                else
                {
                    TestSource1Material(path, originalPath, package, GetSharedSource1TestGame(diskPath));
                }
            }
            catch (Exception e)
            {
                LogException(e, path, originalPath);
            }

            return true;
        }

        private void TestSource1Texture(string path, Stream stream, string? originalPath)
        {
            var vtf = new VtfFile(stream);

            using var resource = Source1TextureConverter.Convert(vtf);
            resource.FileName = path;

            TestAndCollectStats(resource, path, originalPath, null, null);
        }

        private void TestSource1Material(string path, string? originalPath, Package? package, Source1TestGame? game)
        {
            if (game == null)
            {
                AddNonResourceStat("Source 1 material, needs --test_loader", path, originalPath);
                return;
            }

            var gamePath = GetSource1GamePath(path, package, game);

            using var resource = game.Provider.LoadFile(gamePath)
                ?? throw new InvalidDataException($"The material \"{gamePath}\" did not convert.");

            TestAndCollectStats(resource, path, originalPath, game.FileLoader, null);
        }

        private void TestSource1Model(string path, Stream stream, string? originalPath, Package? package, Source1TestGame? game)
        {
            var gamePath = GetSource1GamePath(path, package, game);
            var directory = Path.GetDirectoryName(path);

            // The files of a model come from the same place as the model, since the game can have other builds of them
            Stream? OpenFile(string name)
            {
                if (name.Equals(gamePath, StringComparison.OrdinalIgnoreCase))
                {
                    stream.Position = 0;
                    return stream;
                }

                if (package != null)
                {
                    var entry = package.FindEntry(name);

                    if (entry != null)
                    {
                        return package.GetMemoryMappedStreamIfPossible(entry);
                    }
                }
                else if (directory != null)
                {
                    var siblingPath = Path.Combine(directory, Path.GetFileName(name));

                    if (File.Exists(siblingPath))
                    {
                        return new FileStream(siblingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    }
                }

                return game?.FileSystem.OpenFile(name);
            }

            bool MaterialExists(string name) => game?.FileSystem.Exists(name) ?? false;

            var source = Source1Model.Load(gamePath, OpenFile);
            var model = Source1ModelConverter.Convert(source, gamePath, MaterialExists);

            using var resource = model.Resource;
            resource.FileName = path;

            TestAndCollectStats(resource, path, originalPath, game?.FileLoader, null);
        }

        private void TestSource1Map(string path, Stream stream, string? originalPath, string diskPath)
        {
            if (!StatsWithLoader)
            {
                AddNonResourceStat("Source 1 map, needs --test_loader", path, originalPath);
                return;
            }

            // Loading a map mounts its pakfile, so it gets a game of its own
            using var game = CreateSource1TestGame(diskPath)
                ?? throw new InvalidOperationException("A Source 1 map needs the files of its game.");

            BspFile bsp;

            try
            {
                bsp = BspFile.Open(stream, Path.GetFileNameWithoutExtension(path));
            }
            catch (InvalidDataException)
            {
                AddNonResourceStat("Source 1 map with a corrupt lump table", path, originalPath);
                return;
            }

            var map = game.Provider.LoadMap(bsp);
            var mapPath = GetPathInPackage(path, originalPath);

            foreach (var (name, resource) in map.Files)
            {
                try
                {
                    resource.FileName = name;
                    TestAndCollectStats(resource, name, mapPath, game.FileLoader, null);
                }
                catch (Exception e)
                {
                    LogException(e, name, mapPath);
                }
                finally
                {
                    resource.Dispose();
                }
            }
        }

        private static string GetSource1GamePath(string path, Package? package, Source1TestGame? game)
        {
            if (package != null)
            {
                return path;
            }

            return game?.FileSystem.GetGamePath(path) ?? Path.GetFileName(path);
        }

        [GeneratedRegex(@"\.(?:vtf|vmt|mdl|bsp)$", RegexOptions.IgnoreCase)]
        private static partial Regex Source1FileNamesRegex();
    }
}
