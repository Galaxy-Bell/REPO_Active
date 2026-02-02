using System;
using System.IO;
using System.Reflection;
using System.Linq;

public class Program
{
    private static string _managedDir = "";

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: PhotonLocator.exe <path_to_game_Managed_folder>");
            return 1;
        }
        _managedDir = args[0];

        if (!Directory.Exists(_managedDir))
        {
            Console.WriteLine($"[ERROR] Directory not found: {_managedDir}");
            return 1;
        }
        
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;

        try
        {
            var dlls = Directory.GetFiles(_managedDir, "*.dll");
            foreach (var dllPath in dlls)
            {
                try
                {
                    var assembly = Assembly.LoadFrom(dllPath);
                    var targetType = assembly.GetType("ExitGames.Client.Photon.SendOptions", false);
                    if (targetType != null)
                    {
                        Console.WriteLine($"[FOUND] Type 'ExitGames.Client.Photon.SendOptions' was found in: {dllPath}");
                        return 0; // Success
                    }
                }
                catch
                {
                    // Ignore DLLs that can't be loaded
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] An unexpected error occurred: {ex.Message}");
            return 1;
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveAssembly;
        }
        
        Console.WriteLine("[NOT FOUND] The type 'ExitGames.Client.Photon.SendOptions' was not found in any DLL in the specified directory.");
        return 1;
    }

    private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
    {
        var assemblyName = new AssemblyName(args.Name).Name;
        var assemblyPath = Path.Combine(_managedDir, assemblyName + ".dll");
        if (File.Exists(assemblyPath))
        {
            return Assembly.LoadFrom(assemblyPath);
        }
        return null;
    }
}
