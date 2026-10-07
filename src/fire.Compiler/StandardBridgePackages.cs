using fire.Native;
using fire.Package.Manager;

namespace fire.Compiler
{
    /// <summary>
    /// Builds the packages of the standard bridges (<see cref="StandardPackages"/>): for each bridge a <c>fire-name-version.fpk</c> with the prelude (fire source) and the C++ sources of the
    /// bridge (native/bridges/). The build of the solution runs <c>fire.Compiler bridge-packages &lt;folder&gt;</c> after the compiler is built and puts the files into the folder
    /// <c>PackageSource</c> of the output.
    /// </summary>
    public static class StandardBridgePackages
    {
        /// <summary>The C++ files of a bridge (resource names below `bridges/`): its header, and the files in the folder of the same name that it includes.</summary>
        private static IEnumerable<string> NativeResources(string bridge)
        {
            string header = $"bridges/fire_bridge_{bridge}.hpp";
            if (NativeRuntimeFiles.ReadText(header) == null) yield break;
            yield return header;
            foreach (string name in NativeRuntimeFiles.ResourceNames($"bridges/{bridge}/")) yield return name;
        }

        /// <summary>The version of the standard packages: the version of this compiler.</summary>
        public static string Version
        {
            get
            {
                var v = typeof(StandardBridgePackages).Assembly.GetName().Version ?? new Version(1, 0, 0);
                return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
            }
        }

        private static IEnumerable<string> RequiresOf(string bridge) => bridge switch
        {
            "windows" => new[] { "graphics" },
            "ui" => new[] { "graphics", "windows", "reflection" },
            "linq" => new[] { "reflection" },
            "random" => new[] { "time" },
            "http" => new[] { "net", "tls", "io", "time" },
            "tls" => new[] { "net", "io", "time" },
            "net" => new[] { "io", "time" },   // a connection is an IO.Stream; the time limits need the clock
            _ => Array.Empty<string>(),
        };

        /// <summary>The prelude of a bridge: for a bridge that already runs only as a package, its source in the compiler; for the others the built-in one.</summary>
        private static string? PreludeOf(string bridge) => bridge switch
        {
            "time" => fire.Standard.TimePrelude.Source,
            "random" => fire.Standard.RandomPrelude.Source,
            "net" => fire.Standard.NetPrelude.Source,
            "http" => fire.Standard.HttpPrelude.Source,
            "tls" => fire.Standard.TlsPrelude.Source,
            "io" => fire.Standard.IoPrelude.Source,
            "devices" => fire.Standard.DevicesPrelude.Source,
            _ => ImportedPreludes.TrySourceFor(bridge),
        };

        /// <summary>The natives a bridge brings as a package (those the compiler does not know itself yet): the fire name, the number of arguments and the C++ function of the bridge
        /// (see native/bridges/). `list`: the function allocates its result in the list of the scope; `host`: the VM runs it itself.</summary>
        private static IEnumerable<PackageNativeFunction> FunctionsOf(string bridge)
        {
            static PackageNativeFunction F(string name, int arguments, string cpp, bool list = false, bool host = false) =>
                new() { Name = name, Arguments = arguments, Cpp = cpp, NeedsList = list, ReturnsReference = list, Host = host };
            if (bridge == "devices")
            {
                // (`WaitFor`, `WaitForString`: the VM runs them itself and calls `__DEVWaitStep` of the library, see fire.Runtime.DeviceHostNatives)
                foreach (var f in new[]
                {
                    F("__DEVMgrRefresh", 1, "dev::MgrRefresh"), F("__DEVMgrHandleForIdentifier", 1, "dev::MgrHandleForIdentifier"), F("__DEVMgrCount", 0, "dev::MgrCount"),
                    F("__DEVMgrHandleAt", 1, "dev::MgrHandleAt"), F("__DEVMgrIsShared", 0, "dev::MgrIsShared"), F("__DEVMgrDefaultHandle", 0, "dev::MgrDefaultHandle"),
                    F("__DEVIdentifier", 1, "dev::Identifier", list: true), F("__DEVIsShared", 1, "dev::IsShared"), F("__DEVIsConnected", 1, "dev::IsConnected"),
                    F("__DEVPortName", 1, "dev::PortName", list: true), F("__DEVAvailability", 1, "dev::Availability"),
                    F("__DEVTestAvailability", 1, "dev::TestAvailability"), F("__DEVConnect", 1, "dev::Connect"), F("__DEVDisconnect", 1, "dev::Disconnect"),
                    F("__DEVDoCommand", 2, "dev::DoCommand"), F("__DEVHasData", 1, "dev::HasData"), F("__DEVReadString", 1, "dev::ReadString", list: true),
                    F("__DEVRead", 1, "dev::Read", list: true), F("__DEVWriteString", 2, "dev::WriteString"), F("__DEVWrite", 2, "dev::Write"),
                    F("__DEVWaitForString", 3, "dev::WaitForString", host: true), F("__DEVWaitFor", 3, "dev::WaitFor", host: true), F("__DEVWaitStep", 2, "dev::WaitStep"),
                })
                    yield return f;
            }
            if (bridge == "io")
            {
                foreach (var f in new[]
                {
                    F("__IOLastError", 0, "io::LastError"), F("__IOLastErrorMessage", 0, "io::LastErrorMessage", list: true), F("__IOOpenCount", 0, "io::OpenCount"),
                    F("__IOFileOpen", 3, "io::FileOpen"), F("__IOMemNew", 0, "io::MemNew"), F("__IOMemFromBuffer", 1, "io::MemFromBuffer"), F("__IOClose", 1, "io::Close"),
                    F("__IORead", 4, "io::Read"), F("__IOWrite", 4, "io::Write"), F("__IOReadByte", 1, "io::ReadByte"), F("__IOWriteByte", 2, "io::WriteByte"),
                    F("__IOReadRest", 1, "io::ReadRest", list: true), F("__IOFlush", 1, "io::Flush"), F("__IOSeek", 3, "io::Seek"), F("__IOPosition", 1, "io::Position"),
                    F("__IOLength", 1, "io::Length"), F("__IOSetLength", 2, "io::SetLength"), F("__IOCanRead", 1, "io::CanRead"), F("__IOCanWrite", 1, "io::CanWrite"),
                    F("__IOCanSeek", 1, "io::CanSeek"), F("__IOMemToBuffer", 1, "io::MemToBuffer", list: true), F("__IOFileExists", 1, "io::FileExists"),
                    F("__IOFileSize", 1, "io::FileSize"), F("__IOFileTime", 1, "io::FileTime"), F("__IOFileDelete", 1, "io::FileDelete"),
                    F("__IOFileCopy", 3, "io::FileCopy"), F("__IOFileMove", 3, "io::FileMove"), F("__IODirExists", 1, "io::DirExists"),
                    F("__IODirCreate", 1, "io::DirCreate"), F("__IODirDelete", 2, "io::DirDelete"), F("__IODirList", 4, "io::DirList", list: true),
                    F("__IOCurrentDir", 0, "io::CurrentDir", list: true), F("__IOPathCombine", 2, "io::PathCombine", list: true),
                    F("__IOPathFileName", 1, "io::PathFileName", list: true), F("__IOPathStem", 1, "io::PathStem", list: true),
                    F("__IOPathExtension", 1, "io::PathExtension", list: true), F("__IOPathParent", 1, "io::PathParent", list: true),
                    F("__IOPathFull", 1, "io::PathFull", list: true), F("__IOPathTemp", 0, "io::PathTemp", list: true),
                    F("__IOPathSeparator", 0, "io::PathSeparator", list: true), F("__IOPathIsRooted", 1, "io::PathIsRooted"), F("__IOStdHandle", 1, "io::StdHandle"),
                    F("__IOStdWrite", 2, "io::StdWrite"), F("__IOStdFlush", 1, "io::StdFlush"), F("__IOStdReadLine", 0, "io::StdReadLine", list: true),
                    F("__IOStdReadAll", 0, "io::StdReadAll", list: true), F("__IOBufferIndexOf", 4, "io::BufferIndexOf"),
                    F("__IOUtf8Encode", 1, "io::Utf8Encode", list: true), F("__IOUtf8Decode", 3, "io::Utf8Decode", list: true),
                    F("__IOSplitLines", 1, "io::SplitLines", list: true),
                })
                    yield return f;
            }
            if (bridge == "net")
            {
                foreach (var f in new[]
                {
                    F("__NetLastError", 0, "net::LastError"), F("__NetLastErrorMessage", 0, "net::LastErrorMessage", list: true), F("__NetOpenCount", 0, "net::OpenCount"),
                    F("__NetSupported", 0, "net::Supported"), F("__NetNativeHandle", 1, "net::NativeHandle"), F("__NetConnectBegin", 2, "net::ConnectBegin"), F("__NetConnectStep", 1, "net::ConnectStep"), F("__NetTcpListen", 3, "net::TcpListen"), F("__NetAccept", 2, "net::Accept"),
                    F("__NetSend", 5, "net::Send"), F("__NetRecv", 5, "net::Recv"), F("__NetUdpOpen", 2, "net::UdpOpen"), F("__NetSendTo", 6, "net::SendTo"),
                    F("__NetRecvFrom", 5, "net::RecvFrom"), F("__NetPeerHost", 1, "net::PeerHost", list: true), F("__NetPeerPort", 1, "net::PeerPort"),
                    F("__NetLocalHost", 1, "net::LocalHost", list: true), F("__NetLocalPort", 1, "net::LocalPort"), F("__NetAvailable", 1, "net::Available"),
                    F("__NetPoll", 4, "net::Poll"), F("__NetSetOption", 3, "net::SetOption"), F("__NetShutdown", 2, "net::Shutdown"), F("__NetClose", 1, "net::Close"),
                    F("__NetResolve", 1, "net::Resolve", list: true),
                })
                    yield return f;
            }
            if (bridge == "tls")
            {
                foreach (var f in new[]
                {
                    F("__TlsLastError", 0, "tls::LastError"), F("__TlsLastErrorMessage", 0, "tls::LastErrorMessage", list: true), F("__TlsSupported", 0, "tls::Supported"),
                    F("__TlsOpen", 7, "tls::Open"), F("__TlsHandshake", 1, "tls::Handshake"), F("__TlsServerContext", 2, "tls::ServerContext"), F("__TlsFreeContext", 1, "tls::FreeContext"), F("__TlsAccept", 2, "tls::Accept"),
                    F("__TlsRead", 5, "tls::Read"), F("__TlsWrite", 5, "tls::Write"), F("__TlsPending", 1, "tls::Pending"), F("__TlsInfo", 1, "tls::Info", list: true),
                    F("__TlsClose", 1, "tls::Close"),
                })
                    yield return f;
            }
            if (bridge == "time")
            {
                yield return F("Sleep", 1, "sleepNative", host: true);
                yield return F("__time_now", 0, "tm_now");
                yield return F("__time_local_offset", 1, "tm_localOffset");
                yield return F("__time_parts", 1, "tm_parts", list: true);
                yield return F("__time_make", 7, "tm_make");
                yield return F("__time_parse", 1, "tm_parse");
                yield return F("__time_format", 2, "tm_format", list: true);
                yield return F("__time_add_months", 2, "tm_addMonths");
                yield return F("__time_days_in_month", 2, "tm_daysInMonth");
                yield return F("__time_to_ticks", 2, "tm_toTicks");
                yield return F("__time_unit_ticks", 1, "tm_unitTicks", host: true);
                yield return F("__time_span_text", 1, "tm_spanText", list: true);
            }
        }

        /// <summary>The exception classes that the natives of a bridge throw (those of its own prelude, or of the runtime).</summary>
        private static IEnumerable<string> ExceptionsOf(string bridge) => bridge switch
        {
            "time" => new[] { "TimeException" },
            "io" or "devices" => new[] { "DestroyedException" },   // a destroyed buffer is an error of the runtime (the native build then needs the exceptions)
            _ => Array.Empty<string>(),
        };

        /// <summary>Builds the library of the natives of a bridge for this machine (from the package just forged, installed into a store of its own) with a C++ compiler if there is one; the path of the
        /// file in <paramref name="libFolder"/>, or null (no compiler here: nothing is asked in a build).</summary>
        private static string? PrebuiltLibrary(string bridge, string fpk, string libFolder)
        {
            try
            {
                var store = new PackageStore(Path.Combine(Path.GetDirectoryName(libFolder)!, "store"));
                store.Install(fpk);
                var installed = store.FindImport(bridge);
                if (installed == null) return null;
                string built = PackageLibrary.Ensure(installed);
                string dest = Path.Combine(libFolder, PackageLibrary.Rid, Path.GetFileName(built));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(built, dest, overwrite: true);
                return dest;
            }
            catch (PackageException ex)
            {
                Console.Error.WriteLine($"fire-{bridge}: no prebuilt library for {PackageLibrary.Rid} ({ex.Message.Split('\n')[0]})");
                return null;
            }
        }

        /// <summary>Writes the package files into <paramref name="outputFolder"/> (replacing older ones) and returns their paths.</summary>
        public static IReadOnlyList<string> Build(string outputFolder)
        {
            Directory.CreateDirectory(outputFolder);
            var result = new List<string>();
            string work = Path.Combine(Path.GetTempPath(), "fire-bridgepkg-" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (string bridge in StandardPackages.Bridges)
                {
                    string dir = Path.Combine(work, bridge);
                    Directory.CreateDirectory(dir);
                    var import = new PackageImport { Name = bridge, Standard = true, Requires = RequiresOf(bridge).ToList() };
                    string? prelude = PreludeOf(bridge);
                    if (prelude != null)
                    {
                        string file = Path.Combine(dir, bridge + ".fire");
                        File.WriteAllText(file, prelude);
                        import.Prelude = file;
                    }
                    var native = new PackageNative();
                    foreach (string resource in NativeResources(bridge))
                    {
                        string file = Path.Combine(dir, Path.GetFileName(resource));
                        if (File.Exists(file)) continue;
                        File.WriteAllText(file, NativeRuntimeFiles.ReadText(resource)!);
                        native.Sources.Add(file);
                    }
                    native.Functions.AddRange(FunctionsOf(bridge));
                    native.Exceptions.AddRange(ExceptionsOf(bridge));
                    if (bridge == "io") native.Reset = "io::reset";
                    if (bridge == "tls")
                    {
                        native.Reset = "tls::reset";
                        native.LinkLibraries["posix"] = new List<string> { "ssl", "crypto" };   // OpenSSL (an ESP32 gets mbedTLS from ESP-IDF; define FIRE_TLS_MBEDTLS / FIRE_NO_TLS to choose otherwise)
                        native.LinkLibraries["windows"] = new List<string> { "ssl", "crypto", "ws2_32", "crypt32" };
                    }
                    if (bridge == "net")
                    {
                        native.Reset = "net::reset";
                        native.LinkLibraries["windows"] = new List<string> { "ws2_32" };
                    }
                    if (native.Sources.Count > 0) import.Native = native;
                    var manifest = new PackageManifest
                    {
                        Name = StandardPackages.PackageNameOf(bridge), Version = Version, Author = "fire",
                        Description = $"The standard bridge '{bridge}' of fire: prelude" + (import.Native != null ? " and C++ sources." : "."),
                        License = "fire", Imports = { import },
                        Dependencies = RequiresOf(bridge).Select(StandardPackages.PackageNameOf).ToList(),
                    };
                    string forge = Path.Combine(dir, "package.json");
                    manifest.Save(forge);
                    var forged = Fpk.Forge(forge, Path.Combine(dir, "build"));
                    if (import.Native != null && import.Native.Functions.Any(f => !f.Host) && PrebuiltLibrary(bridge, forged.PackagePath, Path.Combine(dir, "lib")) is { } library)
                    {
                        // the library for this machine goes into the package: the users of this machine need no C++ compiler (the other systems build it from the C++ source when they first use it)
                        import.Native.Libraries[PackageLibrary.Rid] = library;
                        manifest.Save(forge);
                        forged = Fpk.Forge(forge, Path.Combine(dir, "build"));
                    }
                    foreach (var old in Directory.GetFiles(outputFolder, $"{manifest.Name}-*{Fpk.Extension}")) File.Delete(old);
                    string target = Path.Combine(outputFolder, Path.GetFileName(forged.PackagePath));
                    File.Copy(forged.PackagePath, target, overwrite: true);
                    result.Add(target);
                }
            }
            finally
            {
                try { Directory.Delete(work, true); } catch (IOException) { }
            }
            return result;
        }
    }
}
