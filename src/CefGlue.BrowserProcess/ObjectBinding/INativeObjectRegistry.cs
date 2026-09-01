namespace Xilium.CefGlue.BrowserProcess.ObjectBinding
{
    internal interface INativeObjectRegistry
    {
        PromiseHolder Bind(string objName, CefV8Context context);
        void Unbind(string objName);
    }
}
