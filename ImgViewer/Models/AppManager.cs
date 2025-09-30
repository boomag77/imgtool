namespace ImgViewer.Models
{
    internal class AppManager : IDisposable
    {

        private readonly WeakReference<IMainView> _mainViewRef;

        public AppManager(IMainView mainView)
        {
            _mainViewRef = new WeakReference<IMainView>(mainView);
        }


        public void Dispose()
        {
            // Dispose of unmanaged resources here if any
            GC.SuppressFinalize(this);
        }

    }
}
