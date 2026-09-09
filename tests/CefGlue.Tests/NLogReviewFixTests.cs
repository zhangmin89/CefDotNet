#if HAS_NLOG
using System.Reflection;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using Xilium.CefGlue.Common.Helpers.Logger;
using CefLogger = Xilium.CefGlue.Common.Helpers.Logger.ILogger;

namespace CefGlue.Tests;

[TestFixture, NonParallelizable]
public class NLogReviewFixTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public void AllLevelsPreserveInterfaceMessagesFormattingAndExceptionObjects(int ordinal)
    {
        var target = new RecordingTarget();
        using var factory = new LogFactory();
        var configuration = new LoggingConfiguration(factory);
        configuration.AddRuleForAllLevels(target);
        factory.Configuration = configuration;
        var native = factory.GetLogger("review-" + ordinal);
        var wrapper = new NLogLogger(native);
        CefLogger logger = wrapper;
        (Action<string> Write, Action<string, object[]> Format, Action<string, Exception> Error, Func<bool> Enabled)[] levels =
        [
            (logger.Trace, wrapper.Trace, logger.TraceException, () => logger.IsTraceEnabled),
            (logger.Debug, wrapper.Debug, logger.DebugException, () => logger.IsDebugEnabled),
            (logger.Info, wrapper.Info, logger.InfoException, () => logger.IsInfoEnabled),
            (logger.Warn, wrapper.Warn, logger.WarnException, () => logger.IsWarnEnabled),
            (logger.Error, wrapper.Error, logger.ErrorException, () => logger.IsErrorEnabled),
            (logger.Fatal, wrapper.Fatal, logger.FatalException, () => logger.IsFatalEnabled)
        ];
        var level = levels[ordinal];
        var exception = new InvalidOperationException("review exception");
        Assert.IsTrue(level.Enabled());
        level.Write("literal {0}");
        level.Format("value {0}", [42]);
        level.Error("failure", exception);
        Assert.AreEqual(3, target.Events.Count);
        CollectionAssert.AreEqual(new[] { "literal {0}", "value 42", "failure" }, target.Events.Select(item => item.FormattedMessage).ToArray());
        Assert.IsTrue(target.Events.All(item => item.Level == LogLevel.FromOrdinal(ordinal) && item.LoggerName == native.Name));
        Assert.AreSame(exception, target.Events[2].Exception);
        Assert.AreSame(native, Underlying(wrapper));
    }

    [Test]
    public void InitializerIsLazyAndItsResultIsReused()
    {
        using var factory = new LogFactory();
        var native = factory.GetLogger("initializer");
        var initializer = new Initializer(native);
        var wrapper = new NLogLogger(initializer);
        Assert.AreEqual(0, initializer.Calls);
        Assert.AreSame(native, Underlying(wrapper));
        Assert.AreSame(native, Underlying(wrapper));
        Assert.AreEqual(1, initializer.Calls);
        Assert.AreEqual("named-review", Underlying(new NLogLogger("named-review")).Name);
    }

    [Test]
    public void LegacyTypeConstructorPreservesPrivateConstructorsAndTypeSpecificCache()
    {
        var first = Underlying(new NLogLogger(typeof(PublicLogger)));
        var second = Underlying(new NLogLogger(typeof(PrivateLogger)));
        Assert.AreEqual(typeof(PublicLogger), first.GetType());
        Assert.AreEqual(typeof(PrivateLogger), second.GetType());
        Assert.AreEqual(first.Name, second.Name);
        StringAssert.EndsWith("TypeLogInitilizer", first.Name);
        Assert.AreNotEqual(typeof(PublicLogger).FullName, first.Name);
        Assert.AreSame(LogManager.LogFactory, first.Factory);
        Assert.AreNotSame(first, second);
        Assert.AreSame(first, Underlying(new NLogLogger(typeof(PublicLogger))));
        Assert.AreSame(second, Underlying(new NLogLogger(typeof(PrivateLogger))));
    }

    [Test]
    public void NullLoggerTypeRetainsTheBaseLogger()
    {
        Assert.AreEqual(typeof(NLog.Logger), Underlying(new NLogLogger((Type)null!)).GetType());
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void InvalidLoggerTypesRetainNLogsErrorPolicy(bool constructorThrows, bool propagate)
    {
        var previousExceptions = LogManager.ThrowExceptions;
        var previousConfigurationExceptions = LogManager.ThrowConfigExceptions;
        try
        {
            LogManager.ThrowExceptions = propagate;
            LogManager.ThrowConfigExceptions = propagate;
            var type = constructorThrows
                ? (propagate ? typeof(ThrowingLogger<Propagate>) : typeof(ThrowingLogger<Tolerate>))
                : (propagate ? typeof(NotLogger<Propagate>) : typeof(NotLogger<Tolerate>));
            var wrapper = new NLogLogger(type);
            if (propagate)
            {
                var original = typeof(LogManager).GetMethod("GetCurrentClassLogger", new[] { typeof(Type) })!;
                var baseline = Assert.Throws<TargetInvocationException>(() => original.Invoke(null, new object[] { type }));
                var actual = Assert.Throws(baseline!.InnerException!.GetType(), () => _ = wrapper.IsInfoEnabled);
                if (constructorThrows)
                {
                    Assert.IsInstanceOf<InvalidOperationException>(actual!.InnerException);
                    Assert.AreEqual("constructor failure", actual.InnerException!.Message);
                }
                else StringAssert.Contains("does not inherit from NLog Logger", actual!.Message);
            }
            else Assert.AreEqual(typeof(NLog.Logger), Underlying(wrapper).GetType());
        }
        finally
        {
            LogManager.ThrowExceptions = previousExceptions;
            LogManager.ThrowConfigExceptions = previousConfigurationExceptions;
        }
    }

    private static NLog.Logger Underlying(NLogLogger logger) => (NLog.Logger)typeof(NLogLogger).GetProperty("Log", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(logger)!;

    public class PublicLogger : NLog.Logger { }
    private class PrivateLogger : NLog.Logger { private PrivateLogger() { } }
    private class ThrowingLogger<T> : NLog.Logger { private ThrowingLogger() => throw new InvalidOperationException("constructor failure"); }
    private class NotLogger<T> { }
    private class Propagate { }
    private class Tolerate { }

    private sealed class RecordingTarget : Target
    {
        public List<LogEventInfo> Events { get; } = new();
        protected override void Write(LogEventInfo logEvent) => Events.Add(logEvent);
    }

    private sealed class Initializer(NLog.Logger logger) : ILogInitializer
    {
        public int Calls { get; private set; }
        public NLog.Logger CreateLogger() { Calls++; return logger; }
    }
}
#endif
