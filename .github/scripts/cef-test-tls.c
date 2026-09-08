/* glibc fixes static TLS alignment at process startup. CEF ARM64 needs 64.
 * Preload only this reservation, so tests still load CEF from their own output.
 */
__thread char cef_test_tls_reserve __attribute__((aligned(64), tls_model("initial-exec")));
