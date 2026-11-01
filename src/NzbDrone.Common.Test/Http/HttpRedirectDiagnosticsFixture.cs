using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.TPL;
using HttpClient = NzbDrone.Common.Http.HttpClient;

namespace NzbDrone.Common.Test.Http
{
    [TestFixture]
    public class HttpRedirectDiagnosticsFixture
    {
        [TestCase("magnet")]
        [TestCase("MAGNET")]
        [TestCase("https")]
        [TestCase("relative")]
        [TestCase("multiple")]
        public async Task manual_redirect_omits_sensitive_location_from_logs_without_changing_response(string kind)
        {
            const string secret = "synthetic-secret-redaction-marker";
            const string hash = "1111111111111111111111111111111111111111";
            var location = kind == "https" ? "https://private.synthetic.invalid/" + secret + "?api_key=" + secret :
                kind == "relative" ? "/" + secret + "?api_key=" + secret :
                kind + ":?xt=urn:btih:" + hash + "&tr=" + Uri.EscapeDataString("https://tracker.synthetic.invalid/" + secret + "/announce");
            var request = new HttpRequest("http://request.synthetic.invalid/test") { AllowAutoRedirect = false };
            var header = new HttpHeader();
            header["Location"] = location;
            var expectedLocations = new List<string> { location };
            if (kind == "multiple")
            {
                var secondLocation = "https://private.synthetic.invalid/second?api_key=" + secret;
                header.Add("Location", secondLocation);
                expectedLocations.Add(secondLocation);
            }

            var expected = new HttpResponse(request, header, Array.Empty<byte>(), HttpStatusCode.Found);
            var dispatcher = new Mock<IHttpDispatcher>();
            dispatcher.Setup(x => x.GetResponseAsync(It.IsAny<HttpRequest>(), It.IsAny<CookieContainer>(), It.IsAny<CancellationToken>())).ReturnsAsync(expected);
            var target = new CaptureTarget();
            var configuration = new LoggingConfiguration();
            configuration.AddRule(LogLevel.Trace, LogLevel.Fatal, target);
            using var logs = new LogFactory { Configuration = configuration };
            var client = new HttpClient(Array.Empty<IHttpRequestInterceptor>(), new CacheManager(), Mock.Of<IRateLimitService>(), dispatcher.Object, logs.GetLogger("RedirectDiagnostics"));

            var actual = await client.GetAsync(request);

            actual.Should().BeSameAs(expected);
            actual.Headers.GetValues("Location").Should().Equal(expectedLocations);
            actual.StatusCode.Should().Be(HttpStatusCode.Found);
            request.AllowAutoRedirect.Should().BeFalse();
            dispatcher.Verify(x => x.GetResponseAsync(It.IsAny<HttpRequest>(), It.IsAny<CookieContainer>(), It.IsAny<CancellationToken>()), Times.Once());
            var messages = string.Join("\n", target.Events.Select(x => x.FormattedMessage + x.Exception));
            target.Events.Should().Contain(x => x.Level == LogLevel.Error);
            messages.Should().NotContain(secret).And.NotContain(hash).And.NotContain("magnet:?").And.NotContain("MAGNET:?").And.NotContain("private.synthetic.invalid").And.NotContain("tracker.synthetic.invalid");
        }

        private sealed class CaptureTarget : Target
        {
            public List<LogEventInfo> Events { get; } = new List<LogEventInfo>();

            protected override void Write(LogEventInfo logEvent)
            {
                _ = logEvent.FormattedMessage;
                Events.Add(logEvent);
            }
        }
    }
}
