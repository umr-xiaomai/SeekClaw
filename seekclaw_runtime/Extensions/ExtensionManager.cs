namespace SeekClaw.Runtime.Extensions;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;

/// <summary>
/// Manages runtime extension discovery, initialization, and lifecycle.
/// Core runtime interacts with this manager instead of having hard static
/// dependencies on specific domain modules like Computer Use.
/// </summary>
public sealed class ExtensionManager : IAsyncDisposable
{
    private readonly List<IRuntimeExtension> _extensions = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public IReadOnlyList<IRuntimeExtension> Extensions
    {
        get
        {
            _gate.Wait();
            try { return _extensions.ToList(); }
            finally { _gate.Release(); }
        }
    }

    public void Register(IRuntimeExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        _gate.Wait();
        try
        {
            if (_extensions.All(e => e.Id != extension.Id))
            {
                _extensions.Add(extension);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public IRuntimeExtension? Get(string id)
    {
        _gate.Wait();
        try { return _extensions.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)); }
        finally { _gate.Release(); }
    }

    public bool TryGet(string id, out IRuntimeExtension? extension)
    {
        extension = Get(id);
        return extension is not null;
    }

    /// <summary>
    /// Creates an ExtensionManager with default discovered extensions via reflection.
    /// If an extension module is removed or excluded from compilation, this method
    /// automatically continues with remaining extensions without build or runtime breakage.
    /// </summary>
    [RequiresUnreferencedCode("Discovers runtime extensions dynamically via reflection.")]
    public static ExtensionManager CreateDefault()
    {
        var manager = new ExtensionManager();
        try
        {
            var extensionInterface = typeof(IRuntimeExtension);
            var assembly = extensionInterface.Assembly;
            foreach (var type in assembly.GetTypes())
            {
                if (!type.IsAbstract &&
                    !type.IsInterface &&
                    extensionInterface.IsAssignableFrom(type) &&
                    type.GetConstructor(Type.EmptyTypes) is not null)
                {
                    try
                    {
                        if (Activator.CreateInstance(type) is IRuntimeExtension ext)
                        {
                            manager.Register(ext);
                        }
                    }
                    catch
                    {
                        // Extension discovery failure should never crash runtime initialization
                    }
                }
            }
        }
        catch
        {
            // Assembly scanning safety net
        }

        return manager;
    }

    public void InitializeAll(SeekClawRuntime runtime)
    {
        _gate.Wait();
        try
        {
            foreach (var ext in _extensions)
            {
                try
                {
                    if (ext.IsEnabled(runtime))
                    {
                        ext.Initialize(runtime);
                        ext.RegisterTools(runtime.Tools, runtime);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ExtensionManager] Failed to initialize extension '{ext.Id}': {ex.Message}");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var ext in _extensions)
            {
                try
                {
                    await ext.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ExtensionManager] Failed to dispose extension '{ext.Id}': {ex.Message}");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
