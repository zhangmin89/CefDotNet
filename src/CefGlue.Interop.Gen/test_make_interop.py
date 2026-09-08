import unittest

from cef_parser import obj_header
from make_interop import make_proxy_g_body
import schema


class ProxyReferenceTests(unittest.TestCase):
    def test_inherited_proxy_retains_reference_for_native_call(self):
        header = obj_header()
        header.add_data("cef_request_context.h", """
///
/// Reference counted preferences.
///
/*--cef(source=library)--*/
class CefPreferenceManager : public virtual CefBaseRefCounted {};

///
/// A request context inherits reference counting.
///
/*--cef(source=library)--*/
class CefRequestContext : public CefPreferenceManager {};
""")
        schema.load("cef3", header)
        for name in ("CefPreferenceManager", "CefRequestContext"):
            with self.subTest(name=name):
                body = "\n".join(make_proxy_g_body(header.get_class(name)))
                to_native = body[body.index("ToNative()") :]
                self.assertIn("AddRef();", to_native)
                self.assertLess(to_native.index("AddRef();"), to_native.index("return "))


if __name__ == "__main__":
    unittest.main()
