namespace CefGlue.Tests.Build
{
    internal static class TestRepository
    {
        public static string Root { get; } = FindRoot();

        private static string FindRoot()
        {
            // CallerFilePath is rewritten by CI source-path mapping and is not a runtime location.
            for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "CefDotNet.slnx")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException($"Tests require a repository checkout containing the test assembly: {AppContext.BaseDirectory}");
        }
    }
}
