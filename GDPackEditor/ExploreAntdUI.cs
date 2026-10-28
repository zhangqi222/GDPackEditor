using System;
using System.Reflection;

namespace GDPackEditor
{
    public static class ExploreAntdUI
    {
        public static void Run()
        {
            var assembly = Assembly.Load("AntdUI");
            foreach (var type in assembly.GetTypes())
            {
                if (type.Name.Contains("Table"))
                {
                    Console.WriteLine($"Type: {type.FullName}");
                    foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        Console.WriteLine($"  Property: {prop.Name} ({prop.PropertyType.Name})");
                    }
                    foreach (var nested in type.GetNestedTypes())
                    {
                        Console.WriteLine($"  Nested: {nested.Name}");
                        foreach (var nestedProp in nested.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            Console.WriteLine($"    Property: {nestedProp.Name} ({nestedProp.PropertyType.Name})");
                        }
                    }
                }
            }
        }
    }
}
