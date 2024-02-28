﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ProjBobcat.Class.Helper.TOMLParser;
using ProjBobcat.Class.Model.Fabric;
using ProjBobcat.Class.Model.GameResource;
using ProjBobcat.Class.Model.GameResource.ResolvedInfo;
using SharpCompress.Archives;

namespace ProjBobcat.Class.Helper;

public static class GameResourcesResolveHelper
{
    public static async IAsyncEnumerable<GameModResolvedInfo> ResolveModListAsync(
        IEnumerable<string> files,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) yield break;

            var ext = Path.GetExtension(file);
            if (string.IsNullOrEmpty(ext) ||
                !(ext.Equals(".jar", StringComparison.OrdinalIgnoreCase) ||
                  ext.Equals(".disabled", StringComparison.OrdinalIgnoreCase)))
                continue;

            if (!ArchiveHelper.TryOpen(file, out var archive)) continue;
            if (archive == null) continue;

            var modInfoEntry =
                archive.Entries.FirstOrDefault(e =>
                    e.Key.Equals("mcmod.info", StringComparison.OrdinalIgnoreCase));
            var fabricModInfoEntry =
                archive.Entries.FirstOrDefault(e =>
                    e.Key.Equals("fabric.mod.json", StringComparison.OrdinalIgnoreCase));
            var tomlInfoEntry =
                archive.Entries.FirstOrDefault(e =>
                    e.Key.Equals("META-INF/mods.toml", StringComparison.OrdinalIgnoreCase));

            var isEnabled = ext.Equals(".jar", StringComparison.OrdinalIgnoreCase);

            GameModResolvedInfo? result = null;

            if (modInfoEntry != null)
            {
                result = await GetNewModInfo(modInfoEntry);
                
                if (result != null) goto ReturnResult;
            }

            if (tomlInfoEntry != null)
            {
                result = await GetLegacyModInfo(tomlInfoEntry);

                if (result != null) goto ReturnResult;
            }

            if (fabricModInfoEntry != null)
            {
                result = await GetFabricModInfo(fabricModInfoEntry);
                goto ReturnResult;
            }

            result ??= new GameModResolvedInfo(
                null,
                file,
                ["[!] 未知的数据包类型"],
                Path.GetFileName(file),
                null,
                "Unknown",
                isEnabled);

            ReturnResult:
            yield return result;

            continue;

            async Task<GameModResolvedInfo> GetFabricModInfo(IArchiveEntry entry)
            {
                try
                {
                    await using var stream = entry.OpenEntryStream();
                    var tempModel = await JsonSerializer.DeserializeAsync(stream,
                        FabricModInfoModelContext.Default.FabricModInfoModel, ct);

                    var author = tempModel?.Authors is { Length: > 0 }
                        ? string.Join(',', tempModel.Authors)
                        : null;
                    var modList = tempModel?.Depends?.Select(d => d.Key)?.ToImmutableList();
                    var titleResult = string.IsNullOrEmpty(tempModel?.Id) ? Path.GetFileName(file) : tempModel.Id;
                    var versionResult = string.IsNullOrEmpty(tempModel?.Version) ? null : tempModel.Version;

                    return new GameModResolvedInfo(author, file, modList, titleResult, versionResult, "Fabric", isEnabled);
                }
                catch (JsonException e)
                {
                    var errorList = new[]
                    {
                        "[!] 数据包 JSON 异常",
                        e.Message
                    };
                    return new GameModResolvedInfo(null, file, errorList.ToImmutableList(), Path.GetFileName(file), null, "Fabric", isEnabled);
                }
            }

            async Task<GameModResolvedInfo?> GetNewModInfo(IArchiveEntry entry)
            {
                List<GameModInfoModel>? model = null;
                
                await using var stream = entry.OpenEntryStream();
                var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                
                switch (doc.RootElement.ValueKind)
                {
                    case JsonValueKind.Object:
                        var val = doc.RootElement.Deserialize(GameModInfoModelContext.Default.GameModInfoModel);

                        if (val != null) model = [val];

                        break;
                    case JsonValueKind.Array:
                        model = doc.RootElement.Deserialize(GameModInfoModelContext.Default.ListGameModInfoModel) ?? [];
                        break;
                }

                if (model == null || model.Count == 0)
                    return null;

                var authors = new HashSet<string>();
                foreach (var author in model.Where(m => m.AuthorList != null).SelectMany(m => m.AuthorList!))
                    authors.Add(author);

                var baseMod = model.FirstOrDefault(m => string.IsNullOrEmpty(m.Parent));

                if (baseMod == null)
                {
                    baseMod = model.First();
                    model.RemoveAt(0);
                }
                else
                {
                    model.Remove(baseMod);
                }

                var authorStr = string.Join(',', authors);
                var authorResult = string.IsNullOrEmpty(authorStr) ? null : authorStr;
                var modList = model.Where(m => !string.IsNullOrEmpty(m.Name)).Select(m => m.Name!).ToImmutableList();
                var titleResult = string.IsNullOrEmpty(baseMod.Name) ? Path.GetFileName(file) : baseMod.Name;

                var displayModel = new GameModResolvedInfo(authorResult, file, modList, titleResult, baseMod.Version,
                    "Forge *", isEnabled);

                return displayModel;
            }

            async Task<GameModResolvedInfo?> GetLegacyModInfo(IArchiveEntry entry)
            {
                await using var stream = entry.OpenEntryStream();
                using var sR = new StreamReader(stream);
                using var parser = new TOMLParser.TOMLParser(sR);
                var pResult = parser.TryParse(out var table, out _);

                if (!pResult) return null;
                if (!table.HasKey("mods")) return null;

                var innerTable = table["mods"];

                if (innerTable is not TomlArray arr) return null;
                if (arr.ChildrenCount == 0) return null;

                var infoTable = arr.Children.First();

                var title = infoTable.HasKey("modId")
                    ? (infoTable["modId"]?.AsString ?? "-")
                    : Path.GetFileName(file);
                var author = infoTable.HasKey("authors")
                    ? infoTable["authors"]?.AsString
                    : null;
                var version = infoTable.HasKey("version")
                    ? infoTable["version"]?.AsString
                    : null;

                return new GameModResolvedInfo(author?.Value, file, null, title, version?.Value, "Forge", isEnabled);
            }
        }
    }

    public static async IAsyncEnumerable<GameResourcePackResolvedInfo> ResolveResourcePackAsync(
        IEnumerable<(string, bool)> files,
        [EnumeratorCancellation] CancellationToken ct)
    {
        async Task<GameResourcePackResolvedInfo?> ResolveResPackFile(string file)
        {
            var ext = Path.GetExtension(file);

            if (!ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)) return null;
            if (!ArchiveHelper.TryOpen(file, out var archive)) return null;
            if (archive == null) return null;

            var packIconEntry =
                archive.Entries.FirstOrDefault(e => e.Key.Equals("pack.png", StringComparison.OrdinalIgnoreCase));
            var packInfoEntry = archive.Entries.FirstOrDefault(e =>
                e.Key.Equals("pack.mcmeta", StringComparison.OrdinalIgnoreCase));

            var fileName = Path.GetFileName(file);
            byte[]? imageBytes;
            string? description = null;
            var version = -1;

            if (packIconEntry != null)
            {
                await using var stream = packIconEntry.OpenEntryStream();
                await using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);

                imageBytes = ms.ToArray();
            }
            else
            {
                return null;
            }

            if (packInfoEntry != null)
            {
                try
                {
                    await using var stream = packInfoEntry.OpenEntryStream();
                    var model = await JsonSerializer.DeserializeAsync(stream,
                        GameResourcePackModelContext.Default.GameResourcePackModel, ct);

                    description = model?.Pack?.Description;
                    version = model?.Pack?.PackFormat ?? -1;
                }
                catch (JsonException e)
                {
                    description = $"[!] 数据包 JSON 异常: {e.Message}";
                    version = -1;
                }
            }

            return new GameResourcePackResolvedInfo(fileName, description, version, imageBytes);
        }

        async Task<GameResourcePackResolvedInfo?> ResolveResPackDir(string dir)
        {
            var iconPath = Path.Combine(dir, "pack.png");
            var infoPath = Path.Combine(dir, "pack.mcmeta");

            if (!File.Exists(iconPath)) return null;

            var fileName = dir.Split('\\').Last();
            var imageBytes = await File.ReadAllBytesAsync(iconPath, ct);
            string? description = null;
            var version = -1;

            if (File.Exists(infoPath))
            {
                try
                {
                    await using var contentStream = File.OpenRead(infoPath);
                    var model = await JsonSerializer.DeserializeAsync(contentStream,
                        GameResourcePackModelContext.Default.GameResourcePackModel, ct);

                    description = model?.Pack?.Description;
                    version = model?.Pack?.PackFormat ?? -1;
                }
                catch (JsonException e)
                {
                    description = $"[!] 数据包 JSON 异常: {e.Message}";
                    version = -1;
                }
            }

            return new GameResourcePackResolvedInfo(fileName, description, version, imageBytes);
        }

        foreach (var (path, isDir) in files)
        {
            if (ct.IsCancellationRequested) yield break;

            if (!isDir)
            {
                var result = await ResolveResPackFile(path);

                if (result == null) continue;

                yield return result;
            }
            else
            {
                var result = await ResolveResPackDir(path);

                if (result == null) continue;

                yield return result;
            }
        }
    }

    public static IEnumerable<GameShaderPackResolvedInfo> ResolveShaderPack(IEnumerable<(string, bool)> paths,
        CancellationToken ct)
    {
        GameShaderPackResolvedInfo? ResolveShaderPackFile(string file)
        {
            if (!ArchiveHelper.TryOpen(file, out var archive)) return null;
            if (archive == null) return null;
            if (!archive.Entries.Any(e => e.Key.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase)))
                return null;

            var model = new GameShaderPackResolvedInfo(Path.GetFileName(file), false);

            return model;
        }

        GameShaderPackResolvedInfo? ResolveShaderPackDir(string dir)
        {
            var shaderPath = Path.Combine(dir, "shaders");

            if (!Directory.Exists(shaderPath)) return null;

            return new GameShaderPackResolvedInfo(dir.Split('\\').Last(), true);
        }

        foreach (var (path, isDir) in paths)
        {
            if (ct.IsCancellationRequested) yield break;

            if (!isDir)
            {
                var result = ResolveShaderPackFile(path);

                if (result == null) continue;

                yield return result;
            }
            else
            {
                var result = ResolveShaderPackDir(path);

                if (result == null) continue;

                yield return result;
            }
        }
    }

    public static IEnumerable<GameScreenshotResolvedInfo> ResolveScreenshot(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;

            var extension = Path.GetExtension(file);

            if (string.IsNullOrEmpty(extension)) continue;
            if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase)) continue;

            var fileName = Path.GetFileName(file);

            yield return new GameScreenshotResolvedInfo(file, fileName);
        }
    }
}