﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using CacheCow.Common;
using CacheCow.Common.Helpers;
using CacheCow.Server;
using NUnit.Framework;
using Rhino.Mocks;

namespace CacheCow.Tests.Server
{
    using System.IO;

    public class CachingHandlerTests
	{
		private const string TestUrl = "http://myserver/api/stuff/";
        private const string TestUrl2 = "http://myserver/api/more/";

		private static readonly string[] EtagValues = new[] { "abcdefgh", "12345678" };

		[TestCase("DELETE")]
		[TestCase("PUT")]
		[TestCase("POST")]
		[TestCase("PATCH")]
		public static void TestCacheInvalidation(string method)
		{
			// setup
			var mocks = new MockRepository();
			var request = new HttpRequestMessage(new HttpMethod(method), TestUrl);
			string routePattern = "http://myserver/api/stuffs/*";
			var entityTagStore = mocks.StrictMock<IEntityTagStore>();
			var linkedUrls = new []{"url1", "url2"};
			var cachingHandler = new CachingHandler(entityTagStore)
									{
										LinkedRoutePatternProvider = (url, mthd) => linkedUrls
									};
			var entityTagKey = new CacheKey(TestUrl, new string[0], routePattern);
			var response = new HttpResponseMessage();
			var invalidateCache = cachingHandler.InvalidateCache(entityTagKey, request, response);
			entityTagStore.Expect(x => x.RemoveAllByRoutePattern(routePattern)).Return(1);
			entityTagStore.Expect(x => x.RemoveAllByRoutePattern(linkedUrls[0])).Return(0);
			entityTagStore.Expect(x => x.RemoveAllByRoutePattern(linkedUrls[1])).Return(0);
			mocks.ReplayAll();

			// run
			invalidateCache();

			// verify
			mocks.VerifyAll();
		}

		[TestCase("PUT")]
		[TestCase("POST")]
        [TestCase("PATCH")]
		public static void TestCacheInvalidationForPost(string method)
		{
			// setup
			var locationUrl = new Uri("http://api/SomeLocationUrl");
			var mocks = new MockRepository();
			var request = new HttpRequestMessage(new HttpMethod(method), TestUrl);
			string routePattern = "http://myserver/api/stuffs/*";
			var entityTagStore = mocks.StrictMock<IEntityTagStore>();
			var linkedUrls = new[] { "url1", "url2" };
			var cachingHandler = new CachingHandler(entityTagStore)
			{
				LinkedRoutePatternProvider = (url, mthd) => linkedUrls
			};
			var entityTagKey = new CacheKey(TestUrl, new string[0], routePattern);
			var response = new HttpResponseMessage();
			response.Headers.Location = locationUrl;
			var invalidateCacheForPost = cachingHandler.PostInvalidationRule(entityTagKey, request, response);
			if(method == "POST")
			{
				entityTagStore.Expect(x => x.RemoveAllByRoutePattern(locationUrl.ToString())).Return(1);				
			}
			mocks.ReplayAll();

			// run
			invalidateCacheForPost();

			// verify
			mocks.VerifyAll();

			
		}

        [Test]
        public void Test_NoStore_ResultsIn_ExpiredResourceAndPragmaNoCache()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, TestUrl);
            request.Headers.Add(HttpHeaderNames.Accept, "text/xml");
            var entityTagHeaderValue = new TimedEntityTagHeaderValue("\"12345678\"");
            var cachingHandler = new CachingHandler()
            {              
                ETagValueGenerator = (x, y) => entityTagHeaderValue,
                CacheControlHeaderProvider = (r, c) =>
                                                 {
                                                     return new CacheControlHeaderValue()
                                                                {
                                                                    NoStore = true,
                                                                    NoCache = true
                                                                };
                                                 }
            };
            var response = request.CreateResponse(HttpStatusCode.Accepted);

            cachingHandler.AddCaching(new CacheKey(TestUrl, new string[0]), request, response, 
                new List<KeyValuePair<string, IEnumerable<string>>>())();
            Assert.IsTrue(response.Headers.Pragma.Any(x=>x.Name == "no-cache"), "no-cache not in pragma");

        }

		[TestCase("GET", true, true, true, false, new[] { "Accept", "Accept-Language" })]
		[TestCase("GET", false, true, true, false, new[] { "Accept", "Accept-Language" })]
		[TestCase("PUT", false, true, false, false, new string[0])]
		[TestCase("PUT", false, false, true, true, new[] { "Accept" })]
		public static void AddCaching(string method,
			bool existsInStore,
			bool addVaryHeader,
			bool addLastModifiedHeader,
			bool alreadyHasLastModified,
			string[] varyByHeader)
		{
			// setup 
			var mocks = new MockRepository();
			var request = new HttpRequestMessage(new HttpMethod(method), TestUrl);
			request.Headers.Add(HttpHeaderNames.Accept, "text/xml");
			request.Headers.Add(HttpHeaderNames.AcceptLanguage, "en-GB");
			var entityTagStore = mocks.StrictMock<IEntityTagStore>();
			var entityTagHeaderValue = new TimedEntityTagHeaderValue("\"12345678\"");
			var cachingHandler = new CachingHandler(entityTagStore, varyByHeader)
			{
				AddLastModifiedHeader = addLastModifiedHeader,
				AddVaryHeader = addVaryHeader,
				ETagValueGenerator = (x,y) => entityTagHeaderValue
			};

			
			var entityTagKey = new CacheKey(TestUrl, new[] {"text/xml", "en-GB"}, TestUrl + "/*");

			entityTagStore.Expect(x => x.TryGetValue(Arg<CacheKey>.Matches(etg => etg.ResourceUri == TestUrl),
				out Arg<TimedEntityTagHeaderValue>.Out(entityTagHeaderValue).Dummy)).Return(existsInStore);

			if (!existsInStore)
			{
				entityTagStore.Expect(
					x => x.AddOrUpdate(Arg<CacheKey>.Matches(etk => etk == entityTagKey),
						Arg<TimedEntityTagHeaderValue>.Matches(ethv => ethv.Tag == entityTagHeaderValue.Tag)));
			}

			var response = new HttpResponseMessage();
			response.Content = new ByteArrayContent(new byte[0]);
			if (alreadyHasLastModified)
				response.Content.Headers.Add(HttpHeaderNames.LastModified, DateTimeOffset.Now.ToString("r"));

			var cachingContinuation = cachingHandler.AddCaching(entityTagKey, request, response, request.Headers);
			mocks.ReplayAll();

			// run
			cachingContinuation();

			// verify

			// test kast modified only if it is GET and PUT
			if (addLastModifiedHeader && method.IsIn("PUT", "GET"))
			{
				Assert.That(response.Content.Headers.Any(x => x.Key == HttpHeaderNames.LastModified),
					"LastModified does not exist");
			}
			if (!addLastModifiedHeader && !alreadyHasLastModified)
			{
				Assert.That(!response.Content.Headers.Any(x => x.Key == HttpHeaderNames.LastModified),
					"LastModified exists");
			}
			mocks.VerifyAll();

		}

        [TestCase("DELETE")]
        [TestCase("PUT")]
        [TestCase("POST")]
        [TestCase("PATCH")]
        public static void TestManualInvalidation(string method)
        {
            // setup
            var mocks = new MockRepository();
            
            string routePattern1 = "http://myserver/api/stuffs/*";
            string routePattern2 = "http://myserver/api/more/*";
            
            var entityTagStore = mocks.StrictMock<IEntityTagStore>();
            var linkedUrls = new[] { "url1", "url2" };
            var cachingHandler = new CachingHandler(entityTagStore)
            {
                LinkedRoutePatternProvider = (url, mthd) => linkedUrls,
                CacheKeyGenerator = (url, headers) =>
                    {
                        if (url == "/api/stuff/") return new CacheKey(url, headers.SelectMany(h => h.Value), routePattern1);
                        if (url == "/api/more/") return new CacheKey(url, headers.SelectMany(h => h.Value), routePattern2);
                        throw new ArgumentException();
                    }
            };

            entityTagStore.Expect(x => x.RemoveAllByRoutePattern(routePattern1)).Return(1);
            entityTagStore.Expect(x => x.RemoveAllByRoutePattern(routePattern2)).Return(1);

            entityTagStore.Expect(x => x.RemoveAllByRoutePattern(linkedUrls[0])).Return(0);
            entityTagStore.Expect(x => x.RemoveAllByRoutePattern(linkedUrls[1])).Return(0);
            mocks.ReplayAll();

            // run
            cachingHandler.InvalidateResources(new HttpMethod(method), new Uri(TestUrl), new Uri(TestUrl2));

            // verify
            mocks.VerifyAll();
        }
	}
}
