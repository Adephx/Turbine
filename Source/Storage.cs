using System.IO;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;

namespace Turbine;

public static class Storage
{
	private static readonly JsonSerializerOptions Json = new()
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true
	};

	public static string Inside(string root, string relative)
	{
		if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
			throw new IOException("A relative path inside the portable folder is required.");
		root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
		var path = Path.GetFullPath(Path.Combine(root, relative));
		var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
		if (Path.TrimEndingDirectorySeparator(path).Equals(root, StringComparison.OrdinalIgnoreCase) ||
			!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			throw new IOException("The path must be inside the portable folder.");
		CheckLinks(root);
		var current = root;
		foreach (var segment in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
		{
			current = Path.Combine(current, segment);
			CheckLinks(current);
		}

		return path;
	}

	public static void CheckLinks(string path)
	{
		if (Directory.Exists(path) || File.Exists(path))
			CheckLinks(path, File.GetAttributes(path));
	}

	private static void CheckLinks(string path, FileAttributes attributes)
	{
		if ((attributes & FileAttributes.ReparsePoint) != 0)
			throw new IOException($"Linked folders/files are not supported: {path}");
	}

	public static List<string> Files(string root, CancellationToken cancel = default, string searchPattern = "*") => Files(root, out _, cancel, searchPattern);

	public static List<string> Files(string root, out long totalBytes, CancellationToken cancel = default, string searchPattern = "*")
	{
		cancel.ThrowIfCancellationRequested();
		CheckLinks(root);
		var result = new List<string>();
		long size = 0;

		void Visit(DirectoryInfo directory)
		{
			cancel.ThrowIfCancellationRequested();
			foreach (var file in directory.EnumerateFiles(searchPattern))
			{
				cancel.ThrowIfCancellationRequested();
				CheckLinks(file.FullName, file.Attributes);
				result.Add(file.FullName);
				size += file.Length;
			}

			foreach (var subdirectory in directory.EnumerateDirectories())
			{
				cancel.ThrowIfCancellationRequested();
				CheckLinks(subdirectory.FullName, subdirectory.Attributes);
				Visit(subdirectory);
			}
		}

		var directory = new DirectoryInfo(root);
		if (directory.Exists)
			Visit(directory);
		totalBytes = size;
		return result;
	}

	public static T Read<T>(string path)
		where T : new()
	{
		if (!File.Exists(path))
			return new T();
		try
		{
			return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new JsonException("Empty JSON.");
		}
		catch (JsonException ex)
		{
			throw new IOException($"Cannot read {path}. The original file has been kept.\n{ex.Message}", ex);
		}
	}

	public static void Write<T>(string path, T value, FileSecurity? access = null) => WriteText(path, JsonSerializer.Serialize(value, Json), access);

	public static void WriteText(string path, string text, FileSecurity? access = null) => WriteBytes(path, new UTF8Encoding(false).GetBytes(text), access);

	public static void WriteBytes(string path, byte[] bytes, FileSecurity? access = null)
	{
		CheckLinks(path);
		if (File.Exists(path) && new FileInfo(path).Length == bytes.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
		{
			if (access != null)
				new FileInfo(path).SetAccessControl(access);
			return;
		}
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			if (access == null)
				File.WriteAllBytes(temp, bytes);
			else
			{
				using var stream = new FileInfo(temp).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, access);
				stream.Write(bytes);
			}
			File.Move(temp, path, true);
		}
		finally
		{
			if (File.Exists(temp))
				File.Delete(temp);
		}
	}

	public static async Task CopyTree(string source, string destination, CancellationToken cancel)
	{
		var files = Files(source, cancel);

		void CopyDirectories(string from, string to)
		{
			cancel.ThrowIfCancellationRequested();
			Directory.CreateDirectory(to);
			foreach (var dir in Directory.EnumerateDirectories(from))
				CopyDirectories(dir, Inside(to, Path.GetFileName(dir)));
		}

		CopyDirectories(source, destination);
		foreach (var file in files)
		{
			cancel.ThrowIfCancellationRequested();
			var relative = Path.GetRelativePath(source, file);
			var target = Inside(destination, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
			await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
			await input.CopyToAsync(output, cancel);
		}
	}

	public static void DeleteTree(string root, string relative)
	{
		var path = Inside(root, relative);
		if (!Directory.Exists(path))
			return;
		var files = Files(path);
		foreach (var file in files)
			File.SetAttributes(file, FileAttributes.Normal);
		Directory.Delete(path, true);
	}
}

public sealed class Ini
{
	private readonly List<string> lines;

	public Ini(string text = "") => lines = string.IsNullOrWhiteSpace(text) ? [] : text.Replace("\r", "").Trim('\n').Split('\n').ToList();

	public void Set(string section, string key, string value)
	{
		var begin = lines.FindIndex(x => x.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
		if (begin < 0)
		{
			lines.Add($"[{section}]");
			lines.Add($"{key}={value}");
			return;
		}

		var end = begin + 1;
		while (end < lines.Count && !lines[end].TrimStart().StartsWith('['))
			end++;
		var found = false;
		for (var i = end - 1; i > begin; i--)
		{
			var part = lines[i].Split('=', 2);
			if (part.Length != 2 || !part[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
				continue;
			if (!found)
			{
				lines[i] = $"{key}={value}";
				found = true;
			}
			else
				lines.RemoveAt(i);
		}

		if (!found)
			lines.Insert(end, $"{key}={value}");
	}

	public void RemoveNumericKeys(string section)
	{
		var inside = false;
		lines.RemoveAll(line =>
		{
			var text = line.Trim();
			if (text.StartsWith('['))
			{
				inside = text.Equals($"[{section}]", StringComparison.OrdinalIgnoreCase);
				return false;
			}

			return inside && uint.TryParse(text.Split('=', 2)[0].Trim(), out _);
		});
	}

	public override string ToString() => string.Join("\n", lines).Trim() + "\n";
}
