﻿using Jose;
using LetsEncrypt.ACME.JOSE;
using LetsEncrypt.ACME.JSON;
using LetsEncrypt.ACME.Messages;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace LetsEncrypt.ACME
{
    /// <summary>
    /// The ACME client encapsulates all the protocol rules to interact
    /// with an ACME client as specified by the ACME specficication.
    /// </summary>
    public class AcmeClient : IDisposable
    {
        #region -- Constants --

        /// <summary>
        /// Regex pattern to match and extract the components of an HTTP related link header.
        /// </summary>
        public static readonly Regex LINK_HEADER_REGEX = new Regex("<(.+)>;rel=\"(.+)\"");
        /// <summary>
        /// The relation name for the "Terms of Service" related link header.
        /// </summary>
        public const string TOS_LINK_REL = "terms-of-service";

        #endregion -- Constants --

        #region -- Fields --

        WebClient _Web;
        JsonSerializerSettings _jsonSettings = new JsonSerializerSettings()
        {
            Formatting = Formatting.Indented,
            ContractResolver = new AcmeJsonContractResolver(),
        };

        #endregion -- Fields --

        #region -- Properties --

        public Uri RootUrl
        { get; set; }

        public ISigner Signer
        { get; set; }

        public AcmeRegistration Registration
        { get; set; }

        public bool Initialized
        { get; private set; }

        private WebClient Web
        {
            get
            {
                if (_Web == null)
                {
                    _Web = new WebClient();
                    _Web.BaseAddress = RootUrl.ToString();
                    _Web.Encoding = Encoding.UTF8;
                    _Web.Headers["content-type"] = "application/json";
                }
                return _Web;
            }
        }

        public string NextNonce
        { get; private set; }

        #endregion -- Properties --

        #region -- Methods --
        
        public void Init()
        {
            var requ = WebRequest.Create(new Uri(RootUrl, "/"));

            // TODO:  according to ACME 5.5 we *should* be able to issue a HEAD
            // request to get an initial replay-nonce, but this is not working,
            // so we do a GET against the root URL to get that initial nonce
            //requ.Method = "HEAD";
            requ.Method = "GET";

            var resp = requ.GetResponse();
            ExtractNonce(resp);

            Initialized = true;
        }

        public void Dispose()
        {
            if (Web != null)
                Web.Dispose();

            Initialized = false;
        }

        protected void AssertInit()
        {
            if (!Initialized)
                throw new InvalidOperationException("Client is not initialized");
        }

        protected void AssertRegistration()
        {
            if (Registration == null)
                throw new InvalidOperationException("Client is missing registration info");
        }

        public IDictionary<string, string> GetDirectory()
        {
            AssertInit();

            var resp = Web.DownloadString("/acme/directory");

            //var requ = WebRequest.Create(RootUrl);
            //requ.ContentType = "application/json";

            //var resp = requ.GetResponse();

            return null;
        }

        public AcmeRegistration Register(string[] contacts)
        {
            AssertInit();

            var requMsg = new NewRegRequest
            {
                Contact = contacts,
            };

            var resp = PostRequest(new Uri(RootUrl, "/acme/new-reg"), requMsg);

            // HTTP 409 (Conflict) response for a previously registered pub key
            //    Location:  still had the regUri
            if (resp.IsError)
            {
                if (resp.StatusCode == HttpStatusCode.Conflict)
                    throw new AcmeWebException(resp.Error as WebException,
                            "Conflict due to previously registered public key", resp);
                else if (resp.IsError)
                    throw new AcmeWebException(resp.Error as WebException,
                            "Unexpected error", resp);
            }

            var regUri = resp.Headers["Location"];
            if (string.IsNullOrEmpty(regUri))
                throw new AcmeException("server did not provide a registration URI in the response");

            // TODO:  Link headers can be returned:
            //   HTTP/1.1 201 Created
            //   Content-Type: application/json
            //   Location: https://example.com/acme/reg/asdf
            //   Link: <https://example.com/acme/new-authz>;rel="next"
            //   Link: <https://example.com/acme/recover-reg>;rel="recover"
            //   Link: <https://example.com/acme/terms>;rel="terms-of-service"
            //
            // The "terms-of-service" URI should be included in the "agreement" field
            // in a subsequent registration update
            var links = resp.Headers["Link"];
            var tosUri = ExtractTosLinkUri(resp);

            var respMsg = JsonConvert.DeserializeObject<RegResponse>(resp.Content);

            var newReg = new AcmeRegistration
            {
                PublicKey = Signer.ExportJwk(),
                RegistrationUri = regUri,
                Contacts = respMsg.Contact,
                Links = string.IsNullOrEmpty(links)
                        ? null
                        : links.Split(','),
                TosLinkUri = tosUri,
                AuthorizationsUri = respMsg.Authorizations,
                CertificatesUri = respMsg.Certificates,
                TosAgreementUri = respMsg.Agreement,
            };

            Registration = newReg;

            return Registration;
        }

        public AcmeRegistration UpdateRegistration(bool useRootUrl = false, bool agreeToTos = false, string[] contacts = null)
        {
            AssertInit();
            AssertRegistration();

            var requMsg = new UpdateRegRequest();

            if (contacts != null)
                requMsg.Contact = contacts;

            if (agreeToTos && !string.IsNullOrWhiteSpace(Registration.TosLinkUri))
                requMsg.Agreement = Registration.TosLinkUri;

            // Compute the URL to submit the request to, either exactly as
            // provided in the Registration object or relative to the Root URL
            var requUri = new Uri(Registration.RegistrationUri);
            if (useRootUrl)
                requUri = new Uri(RootUrl, requUri.PathAndQuery);

            var resp = PostRequest(requUri, requMsg);

            if (resp.IsError)
            {
                if (resp.StatusCode == HttpStatusCode.Conflict)
                    throw new AcmeWebException(resp.Error as WebException,
                            "Conflict due to previously registered public key", resp);
                else if (resp.IsError)
                    throw new AcmeWebException(resp.Error as WebException,
                            "Unexpected error", resp);
            }

            var links = resp.Headers["Link"];
            var tosUri = ExtractTosLinkUri(resp);

            var respMsg = JsonConvert.DeserializeObject<RegResponse>(resp.Content);

            var updReg = new AcmeRegistration
            {
                PublicKey = Signer.ExportJwk(),
                RegistrationUri = Registration.RegistrationUri,
                Contacts = respMsg.Contact,
                Links = string.IsNullOrEmpty(links)
                        ? null
                        : links.Split(','),
                TosLinkUri = tosUri,
                AuthorizationsUri = respMsg.Authorizations,
                CertificatesUri = respMsg.Certificates,
                TosAgreementUri = respMsg.Agreement,
            };

            Registration = updReg;

            return Registration;
        }

        public void AuthorizeDns(string dnsIdentifier)
        {
            AssertInit();
            AssertRegistration();

            var requMsg = new NewAuthzRequest
            {
                Identifier = new IdentifierPart
                {
                    Type = "dns",
                    Value = dnsIdentifier
                }
            };

            var resp = PostRequest(new Uri(RootUrl, "/acme/new-authz"), requMsg);
            var respMsg = JsonConvert.DeserializeObject<NewAuthzResponse>(resp.Content);
        }

        /// <summary>
        /// Submits an ACME protocol request via an HTTP post with the necessary semantics
        /// and protocol details.  The result is a simplified and canonicalized response
        /// object capturing the error state, HTTP response headers and content of the
        /// response body.
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        private PostResponse PostRequest(Uri uri, object message)
        {
            var acmeSigned = ComputeAcmeSigned(message, Signer);
            var acmeBytes = Encoding.ASCII.GetBytes(acmeSigned);

            var requ = WebRequest.Create(uri);
            requ.Method = "POST";
            requ.ContentType = "application/json";
            requ.ContentLength = acmeBytes.Length;
            using (var s = requ.GetRequestStream())
            {
                s.Write(acmeBytes, 0, acmeBytes.Length);
            }
            
            try
            {
                using (var resp = (HttpWebResponse)requ.GetResponse())
                {
                    ExtractNonce(resp);
                    return new PostResponse(resp);
                }
            }
            catch (WebException ex)
            {
                using (var resp = (HttpWebResponse)ex.Response)
                {
                    return new PostResponse(resp)
                    {
                        IsError = true,
                        Error = ex,
                    };
                }
            }
        }

        /// <summary>
        /// Computes the JWS-signed ACME request body for the given message object
        /// and signer instance.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="signer"></param>
        /// <returns></returns>
        private string ComputeAcmeSigned(object message, ISigner signer)
        {
            var protectedHeader = new
            {
                nonce = NextNonce
            };
            var unprotectedHeader = new
            {
                alg = Signer.JwsAlg,
                jwk = Signer.ExportJwk()
            };
            var payload = JsonConvert.SerializeObject(message);
            var acmeSigned = JwsHelper.SignFlatJson(Signer.Sign, payload,
                    protectedHeader, unprotectedHeader);

            return acmeSigned;
        }

        /// <summary>
        /// Extracts the next ACME protocol nonce from the argument Web response
        /// and remembers it for the next protocol request.
        /// </summary>
        /// <param name="resp"></param>
        private void ExtractNonce(WebResponse resp)
        {
            var nonceHeader = resp.Headers.AllKeys.FirstOrDefault(x =>
                    x.Equals("Replay-nonce", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(nonceHeader))
                throw new AcmeException("Missing initial replay-nonce header");

            NextNonce = resp.Headers[nonceHeader];
            if (string.IsNullOrEmpty(NextNonce))
                throw new AcmeException("Missing initial replay-nonce header value");
        }

        /// <summary>
        /// Extracts the "Terms of Service" related link header if there is one and
        /// returns the URI associated with it.  Otherwise returns <c>null</c>.
        /// </summary>
        /// <param name="resp"></param>
        /// <returns></returns>
        private string ExtractTosLinkUri(PostResponse resp)
        {
            var links = resp.Headers.GetValues("Link");

            if (links != null && links.Length > 0)
            {
                // We're looking for something like this:
                //     <http://localhost:4000/terms/v1>;rel=\"terms-of-service\"
                foreach (var l in links)
                {
                    var m = LINK_HEADER_REGEX.Match(l);
                    if (m.Success)
                    {
                        if (TOS_LINK_REL.Equals(m.Groups[2].Value))
                            return m.Groups[1].Value;
                    }
                }
            }

            return null;
        }

        #endregion -- Methods --

        #region -- Nested Types --

        public class PostResponse
        {
            public PostResponse(HttpWebResponse resp)
            {
                StatusCode = resp.StatusCode;
                Headers = resp.Headers;
                using (var s = new StreamReader(resp.GetResponseStream()))
                {
                    Content = s.ReadToEnd();
                }
            }

            public bool IsError
            { get; set; }

            public Exception Error
            { get; set; }

            public HttpStatusCode StatusCode
            { get; set; }

            public WebHeaderCollection Headers
            { get; set; }
            
            public string Content
            { get; set; }


        }

        public class AcmeWebException : AcmeException
        {
            public AcmeWebException(WebException innerException, string message = null,
                    PostResponse response = null) : base(message, innerException)
            {
                Response = response;
            }

            protected AcmeWebException(SerializationInfo info, StreamingContext context) : base(info, context)
            { }

            public WebException WebException
            {
                get { return InnerException as WebException; }
            }

            public PostResponse Response
            { get; private set; }
        }


        #endregion -- Nested Types --
    }
}
