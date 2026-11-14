using System.Windows.Controls;

namespace BlackPearl.Desktop;

// The library's categories sit on the app's thread selector (ThreadBar), which lights the current one.
public partial class MainWindow
{
    private Button[] LibraryCategoryButtons => [AllLibraryNav, AnimeLibraryNav, ShowsLibraryNav, MoviesLibraryNav, UnavailableLibraryNav, MissingLibraryNav, UnmatchedLibraryNav];
}
