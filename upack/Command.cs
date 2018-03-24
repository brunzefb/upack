﻿using Inedo.UPack;
using Inedo.UPack.Net;
using Inedo.UPack.Packaging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Inedo.ProGet.UPack
{
    public abstract class Command
    {
        [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
        public sealed class PositionalArgumentAttribute : Attribute
        {
            public int Index { get; }
            public bool Optional { get; set; } = false;

            public PositionalArgumentAttribute(int index)
            {
                this.Index = index;
            }
        }

        [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
        public sealed class ExtraArgumentAttribute : Attribute
        {
            public bool Optional { get; set; } = true;
        }

        [AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = true)]
        public sealed class AlternateNameAttribute : Attribute
        {
            public string Name { get; }

            public AlternateNameAttribute(string name)
            {
                this.Name = name;
            }
        }

        public abstract class Argument
        {
            protected readonly PropertyInfo p;

            internal Argument(PropertyInfo p)
            {
                this.p = p;
            }

            public abstract bool Optional { get; }
            public string DisplayName => p.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? p.Name;
            public string Description => p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
            public object DefaultValue => p.GetCustomAttribute<DefaultValueAttribute>()?.Value;

            public abstract string GetUsage();

            public virtual string GetHelp()
            {
                return $"{this.DisplayName} - {this.Description}";
            }

            public bool TrySetValue(Command cmd, string value)
            {
                if (p.PropertyType == typeof(bool))
                {
                    if (string.IsNullOrEmpty(value))
                    {
                        p.SetValue(cmd, true);
                        return true;
                    }
                    bool result;
                    if (bool.TryParse(value, out result))
                    {
                        p.SetValue(cmd, result);
                        return true;
                    }
                    Console.WriteLine($@"--{this.DisplayName} must be ""true"" or ""false"".");
                    return false;
                }

                if (p.PropertyType == typeof(string))
                {
                    p.SetValue(cmd, value);
                    return true;
                }

                if (p.PropertyType == typeof(NetworkCredential))
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        p.SetValue(cmd, null);
                        return true;
                    }

                    var parts = value.Split(new[] { ':' }, 2);
                    if (parts.Length != 2)
                    {
                        Console.WriteLine($@"--{this.DisplayName} must be in the format ""username:password"".");
                        return false;
                    }

                    p.SetValue(cmd, new NetworkCredential(parts[0], parts[1]));
                    return true;
                }

                throw new ArgumentException(p.PropertyType.FullName);
            }
        }

        public sealed class PositionalArgument : Argument
        {
            internal PositionalArgument(PropertyInfo p) : base(p)
            {
            }

            public int Index => p.GetCustomAttribute<PositionalArgumentAttribute>().Index;
            public override bool Optional => p.GetCustomAttribute<PositionalArgumentAttribute>().Optional;

            public override string GetUsage()
            {
                var s = $"«{this.DisplayName}»";

                if (this.Optional)
                {
                    s = $"[{s}]";
                }

                return s;
            }
        }

        public sealed class ExtraArgument : Argument
        {
            internal ExtraArgument(PropertyInfo p) : base(p)
            {
            }

            public IEnumerable<string> AlternateNames => p.GetCustomAttributes<AlternateNameAttribute>().Select(a => a.Name);
            public override bool Optional => p.GetCustomAttribute<ExtraArgumentAttribute>().Optional;

            public override string GetUsage()
            {
                var s = $"--{this.DisplayName}=«{this.DisplayName}»";

                if (this.Optional)
                {
                    s = $"[{s}]";
                }

                if (p.PropertyType == typeof(bool) && this.DefaultValue.Equals(false) && this.Optional)
                {
                    s = $"[--{this.DisplayName}]";
                }

                return s;
            }
        }

        public string DisplayName => this.GetType().GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? this.GetType().Name;
        public string Description => this.GetType().GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
        public IEnumerable<PositionalArgument> PositionalArguments => this.GetType().GetRuntimeProperties()
            .Where(p => p.GetCustomAttribute<PositionalArgumentAttribute>() != null)
            .Select(p => new PositionalArgument(p))
            .OrderBy(a => a.Index);

        public abstract Task<int> RunAsync();

        public IEnumerable<ExtraArgument> ExtraArguments => this.GetType().GetRuntimeProperties()
            .Where(p => p.GetCustomAttribute<ExtraArgumentAttribute>() != null)
            .Select(p => new ExtraArgument(p));

        public string GetUsage()
        {
            var s = new StringBuilder("upack ");

            s.Append(this.DisplayName);

            foreach (var arg in this.PositionalArguments)
            {
                s.Append(' ').Append(arg.GetUsage());
            }

            foreach (var arg in this.ExtraArguments)
            {
                s.Append(' ').Append(arg.GetUsage());
            }

            return s.ToString();
        }

        public string GetHelp()
        {
            var s = new StringBuilder("Usage: ");

            s.AppendLine(this.GetUsage()).AppendLine().AppendLine(this.Description);

            foreach (var arg in this.PositionalArguments)
            {
                s.AppendLine().Append(arg.GetHelp());
            }

            foreach (var arg in this.ExtraArguments)
            {
                s.AppendLine().Append(arg.GetHelp());
            }

            return s.ToString();
        }

        internal static async Task<UniversalPackageMetadata> ReadManifestAsync(Stream metadataStream)
        {
            var text = await new StreamReader(metadataStream).ReadToEndAsync();
            return JsonConvert.DeserializeObject<UniversalPackageMetadata>(text);
        }

        internal static void PrintManifest(UniversalPackageMetadata info)
        {
            if (!string.IsNullOrEmpty(info.Group))
                Console.WriteLine($"Package: {info.Group}:{info.Name}");
            else
                Console.WriteLine($"Package: {info.Name}");

            Console.WriteLine($"Version: {info.Version}");
        }

        internal static async Task UnpackZipAsync(string targetDirectory, bool overwrite, UniversalPackage package, bool preserveTimestamps)
        {
            Directory.CreateDirectory(targetDirectory);

            var entries = package.Entries.Where(e => e.IsContent);

            int files = 0;
            int directories = 0;

            foreach (var entry in entries)
            {
                var targetPath = Path.Combine(targetDirectory, entry.ContentPath);

                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(targetPath);
                    directories++;
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                    using (var entryStream = entry.Open())
                    using (var targetStream = new FileStream(targetPath, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                    {
                        await entryStream.CopyToAsync(targetStream);
                    }

                    // Assume files with timestamps set to 0 (DOS time) or close to 0 are not timestamped.
                    if (preserveTimestamps && entry.Timestamp.Year > 1980)
                    {
                        File.SetLastWriteTimeUtc(targetPath, entry.Timestamp.DateTime);
                    }

                    files++;
                }
            }

            Console.WriteLine($"Extracted {files} files and {directories} directories.");
        }

        internal static async Task<UniversalPackageVersion> GetVersionAsync(UniversalFeedClient client, UniversalPackageId id, string version, bool prerelease)
        {
            if (!string.IsNullOrEmpty(version) && !string.Equals(version, "latest", StringComparison.OrdinalIgnoreCase) && !prerelease)
            {
                var parsed = UniversalPackageVersion.TryParse(version);
                if (parsed != null)
                    return parsed;

                throw new ApplicationException($"Invalid UPack version number: {version}");
            }

            IReadOnlyList<RemoteUniversalPackageVersion> versions;
            try
            {
                versions = await client.ListPackageVersionsAsync(id);
            }
            catch (WebException ex)
            {
                throw ConvertWebException(ex);
            }

            if (!versions.Any())
                throw new ApplicationException($"No versions of package {id} found.");

            return versions.Max(v => v.Version);
        }

        internal const string PackageNotFoundMessage = "The specified universal package was not found at the given URL";
        internal const string FeedNotFoundMessage = "No UPack feed was found at the given URL";
        internal const string IncorrectCredentialsMessage = "The server rejected the username or password given";

        internal static ApplicationException ConvertWebException(WebException ex, string notFoundMessage = FeedNotFoundMessage)
        {
            var message = ex.Message;
            var statusCode = (ex.Response as HttpWebResponse)?.StatusCode;
            if (ex.Status == WebExceptionStatus.ProtocolError && statusCode.HasValue)
            {
                if (statusCode == HttpStatusCode.NotFound)
                {
                    message = notFoundMessage;
                }
                else if (statusCode == HttpStatusCode.Unauthorized)
                {
                    message = IncorrectCredentialsMessage;
                }

                if (ex.Response.ContentType == "text/plain")
                {
                    try
                    {
                        using (var reader = new StreamReader(ex.Response.GetResponseStream()))
                        {
                            var body = reader.ReadToEnd();
                            if (!string.IsNullOrWhiteSpace(body))
                            {
                                message = message + ": " + body;
                            }
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
            return new ApplicationException(message, ex);
        }

        internal static UniversalFeedClient CreateClient(string source, NetworkCredential credentials)
        {
            try
            {
                var uri = new Uri(source);

                var endpoint = credentials == null ?
                    new UniversalFeedEndpoint(uri, true) :
                    new UniversalFeedEndpoint(uri, credentials.UserName, credentials.SecurePassword);

                return new UniversalFeedClient(endpoint);
            }
            catch (UriFormatException ex)
            {
                throw new ApplicationException("Invalid UPack feed URL: " + ex.Message, ex);
            }
            catch (ArgumentException ex)
            {
                throw new ApplicationException("Invalid UPack feed URL: " + ex.Message, ex);
            }
        }
    }
}
