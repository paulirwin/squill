// See https://aka.ms/new-console-template for more information

using System.Reflection;

Console.WriteLine($"Squill v{typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");

