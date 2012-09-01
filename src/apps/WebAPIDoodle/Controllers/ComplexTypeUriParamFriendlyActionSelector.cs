﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Controllers;
using WebAPIDoodle.Internal;

namespace WebAPIDoodle.Controllers {

    /// <summary>
    /// Reflection based action selector based on ApiControllerActionSelector implementation.
    /// As the private parts cannot be customized, this action selector is very similar with CustomApiControllerActionSelector.
    /// We optimize for the case where we have an <see cref="ComplexTypeUriParamFriendlyActionSelector"/> instance per <see cref="HttpControllerDescriptor"/>
    /// instance but can support cases where there are many <see cref="System.Web.Http.Controllers.HttpControllerDescriptor"/> instances for one 
    /// <see cref="ComplexTypeUriParamFriendlyActionSelector"/> as well. In the latter case the lookup is slightly slower because it goes through
    /// the <see cref="P:System.Web.Http.Controllers.HttpControllerDescriptor.Properties"/> dictionary.
    /// </summary>
    /// <remarks>Most of the code has been taken from <see cref="System.Web.Http.Controllers.ApiControllerActionSelector"/>.</remarks>
    public class ComplexTypeUriParamFriendlyActionSelector : IHttpActionSelector {

        private const string ActionRouteKey = "action";
        private const string ControllerRouteKey = "controller";

        private ActionSelectorCacheItem _fastCache;
        private readonly object _cacheKey = new object();

        public HttpActionDescriptor SelectAction(HttpControllerContext controllerContext) {

            if (controllerContext == null) {

                throw Error.ArgumentNull("controllerContext");
            }

            ActionSelectorCacheItem internalSelector = GetInternalSelector(controllerContext.ControllerDescriptor);
            return internalSelector.SelectAction(controllerContext);
        }

        public ILookup<string, HttpActionDescriptor> GetActionMapping(HttpControllerDescriptor controllerDescriptor) {

            if (controllerDescriptor == null) {
                throw Error.ArgumentNull("controllerDescriptor");
            }

            ActionSelectorCacheItem internalSelector = GetInternalSelector(controllerDescriptor);
            return internalSelector.GetActionMapping();
        }

        private ActionSelectorCacheItem GetInternalSelector(HttpControllerDescriptor controllerDescriptor) {

            // First check in the local fast cache and if not a match then look in the broader 
            // HttpControllerDescriptor.Properties cache
            if (_fastCache == null) {

                ActionSelectorCacheItem selector = new ActionSelectorCacheItem(controllerDescriptor);
                Interlocked.CompareExchange(ref _fastCache, selector, null);
                return selector;
            }
            else if (_fastCache.HttpControllerDescriptor == controllerDescriptor) {

                // If the key matches and we already have the delegate for creating an instance then just execute it
                return _fastCache;
            }
            else {

                // If the key doesn't match then lookup/create delegate in the HttpControllerDescriptor.Properties for
                // that HttpControllerDescriptor instance
                ActionSelectorCacheItem selector = (ActionSelectorCacheItem)controllerDescriptor.Properties.GetOrAdd(
                    _cacheKey,
                    _ => new ActionSelectorCacheItem(controllerDescriptor));

                return selector;
            }
        }

        // All caching is in a dedicated cache class, which may be optionally shared across selector instances.
        // Make this a private nested class so that nobody else can conflict with our state.
        // Cache is initialized during ctor on a single thread. 
        private class ActionSelectorCacheItem {

            private readonly HttpControllerDescriptor _controllerDescriptor;
            private readonly ReflectedHttpActionDescriptor[] _actionDescriptors;
            private readonly IDictionary<ReflectedHttpActionDescriptor, string[]> _actionParameterNames = new Dictionary<ReflectedHttpActionDescriptor, string[]>();
            private readonly ILookup<string, ReflectedHttpActionDescriptor> _actionNameMapping;

            // Selection commonly looks up an action by verb. 
            // Cache this mapping. These caches are completely optional and we still behave correctly if we cache miss. 
            // We can adjust the specific set we cache based on profiler information.
            // Conceptually, this set of caches could be a HttpMethod --> ReflectedHttpActionDescriptor[]. 
            // - Beware that HttpMethod has a very slow hash function (it does case-insensitive string hashing). So don't use Dict.
            // - there are unbounded number of http methods, so make sure the cache doesn't grow indefinitely.  
            // - we can build the cache at startup and don't need to continually add to it. 
            private readonly HttpMethod[] _cacheListVerbKinds = new HttpMethod[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Post };
            private readonly ReflectedHttpActionDescriptor[][] _cacheListVerbs;

            public ActionSelectorCacheItem(HttpControllerDescriptor controllerDescriptor) {

                Contract.Assert(controllerDescriptor != null);

                // Initialize the cache entirely in the ctor on a single thread.
                _controllerDescriptor = controllerDescriptor;

                MethodInfo[] allMethods = _controllerDescriptor.ControllerType.GetMethods(BindingFlags.Instance | BindingFlags.Public);
                MethodInfo[] validMethods = Array.FindAll(allMethods, IsValidActionMethod);

                _actionDescriptors = new ReflectedHttpActionDescriptor[validMethods.Length];
                for (int i = 0; i < validMethods.Length; i++) {

                    MethodInfo method = validMethods[i];
                    ReflectedHttpActionDescriptor actionDescriptor = new ReflectedHttpActionDescriptor(_controllerDescriptor, method);
                    _actionDescriptors[i] = actionDescriptor;
                    HttpActionBinding actionBinding = actionDescriptor.ActionBinding;

                    // Building an action parameter name mapping to compare against the URI parameters coming from the request. 
                    // Here we only take into account required parameters that are simple types and come from URI.
                    _actionParameterNames.Add(
                        actionDescriptor,
                        actionBinding.ParameterBindings
                            .Where(binding => !binding.Descriptor.IsOptional && TypeHelper.IsSimpleUnderlyingType(binding.Descriptor.ParameterType) && binding.WillReadUri())
                            .Select(binding => binding.Descriptor.Prefix ?? binding.Descriptor.ParameterName)
                            .Union(method.IsDefined(typeof(UriParametersAttribute)) ? method.GetCustomAttribute<UriParametersAttribute>().Parameters : Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase).ToArray());
                }

                _actionNameMapping = _actionDescriptors.ToLookup(actionDesc => actionDesc.ActionName, StringComparer.OrdinalIgnoreCase);

                // Bucket the action descriptors by common verbs. 
                int len = _cacheListVerbKinds.Length;
                _cacheListVerbs = new ReflectedHttpActionDescriptor[len][];
                for (int i = 0; i < len; i++) {

                    _cacheListVerbs[i] = FindActionsForVerbWorker(_cacheListVerbKinds[i]);
                }
            }

            public HttpControllerDescriptor HttpControllerDescriptor {
                get { return _controllerDescriptor; }
            }

            public HttpActionDescriptor SelectAction(HttpControllerContext controllerContext) {

                string actionName;
                ReflectedHttpActionDescriptor[] actionsFoundByHttpMethods;
                HttpMethod incomingMethod = controllerContext.Request.Method;
                bool useActionName = controllerContext.RouteData.Values.TryGetValue(ActionRouteKey, out actionName);

                // First get an initial candidate list.
                if (useActionName) {

                    // We have an explicit {action} value, do traditional binding. Just lookup by actionName
                    ReflectedHttpActionDescriptor[] actionsFoundByName = _actionNameMapping[actionName].ToArray();

                    // Throws HttpResponseException with NotFound status because no action matches the Name
                    if (actionsFoundByName.Length == 0) {

                        throw new HttpResponseException(controllerContext.Request.CreateErrorResponse(
                            HttpStatusCode.NotFound,
                            Error.Format(SRResources.ResourceNotFound, controllerContext.Request.RequestUri),
                            Error.Format(SRResources.ApiControllerActionSelector_ActionNameNotFound, _controllerDescriptor.ControllerName, actionName)));
                    }

                    // This filters out any incompatible verbs from the incoming action list
                    actionsFoundByHttpMethods = actionsFoundByName.Where(actionDescriptor => actionDescriptor.SupportedHttpMethods.Contains(incomingMethod)).ToArray();
                }
                else {

                    // No {action} parameter, infer it from the verb.
                    actionsFoundByHttpMethods = FindActionsForVerb(incomingMethod);
                }

                // Throws HttpResponseException with MethodNotAllowed status because no action matches the Http Method

                if (actionsFoundByHttpMethods.Length == 0) {

                    throw new HttpResponseException(controllerContext.Request.CreateErrorResponse(
                        HttpStatusCode.MethodNotAllowed,
                        Error.Format(SRResources.ApiControllerActionSelector_HttpMethodNotSupported, incomingMethod)));
                }

                // Make sure the action parameter matches the route and query parameters. Overload resolution logic is applied when needed.
                IEnumerable<ReflectedHttpActionDescriptor> actionsFoundByParams = FindActionUsingRouteAndQueryParameters(controllerContext, actionsFoundByHttpMethods, useActionName);

                // TODO: Skipping the selecting filters. Do that later
                List<ReflectedHttpActionDescriptor> selectedActions = actionsFoundByParams.ToList();
                actionsFoundByHttpMethods = null;
                actionsFoundByParams = null;

                switch (selectedActions.Count) {
                    case 0:
                        // Throws HttpResponseException with NotFound status because no action matches the request
                        throw new HttpResponseException(controllerContext.Request.CreateErrorResponse(
                            HttpStatusCode.NotFound,
                            Error.Format(SRResources.ResourceNotFound, controllerContext.Request.RequestUri),
                            Error.Format(SRResources.ApiControllerActionSelector_ActionNotFound, _controllerDescriptor.ControllerName)));
                    case 1:
                        return selectedActions[0];
                    default:
                        // Throws exception because multiple actions match the request
                        string ambiguityList = CreateAmbiguousMatchList(selectedActions);
                        throw Error.InvalidOperation(SRResources.ApiControllerActionSelector_AmbiguousMatch, ambiguityList);
                }
            }

            public ILookup<string, HttpActionDescriptor> GetActionMapping() {

                return new LookupAdapter() { Source = _actionNameMapping };
            }

            private IEnumerable<ReflectedHttpActionDescriptor> FindActionUsingRouteAndQueryParameters(HttpControllerContext controllerContext, IEnumerable<ReflectedHttpActionDescriptor> actionsFound, bool hasActionRouteKey) {

                IDictionary<string, object> routeValues = controllerContext.RouteData.Values;
                HashSet<string> routeParameterNames = new HashSet<string>(routeValues.Keys, StringComparer.OrdinalIgnoreCase);
                routeParameterNames.Remove(ControllerRouteKey);
                if (hasActionRouteKey) {

                    routeParameterNames.Remove(ActionRouteKey);
                }

                HttpRequestMessage request = controllerContext.Request;
                Uri requestUri = request.RequestUri;
                bool hasQueryParameters = requestUri != null && !String.IsNullOrEmpty(requestUri.Query);
                bool hasRouteParameters = routeParameterNames.Count != 0;

                if (hasRouteParameters || hasQueryParameters) {

                    var combinedParameterNames = new HashSet<string>(routeParameterNames, StringComparer.OrdinalIgnoreCase);
                    if (hasQueryParameters) {

                        foreach (var queryNameValuePair in request.GetQueryNameValuePairs()) {

                            combinedParameterNames.Add(queryNameValuePair.Key);
                        }
                    }

                    // action parameters is a subset of route parameters and query parameters
                    actionsFound = actionsFound.Where(descriptor => IsSubset(_actionParameterNames[descriptor], combinedParameterNames));

                    if (actionsFound.Count() > 1) {

                        // select the results that match the most number of required parameters 
                        actionsFound = actionsFound
                            .GroupBy(descriptor => _actionParameterNames[descriptor].Length)
                            .OrderByDescending(g => g.Key)
                            .First();
                    }
                }
                else {

                    // return actions with no parameters
                    actionsFound = actionsFound.Where(descriptor => _actionParameterNames[descriptor].Length == 0);
                }

                return actionsFound;
            }

            private static bool IsSubset(string[] actionParameters, HashSet<string> routeAndQueryParameters) {

                foreach (string actionParameter in actionParameters) {

                    if (!routeAndQueryParameters.Contains(actionParameter)) {

                        return false;
                    }
                }

                return true;
            }

            // This is called when we don't specify an Action name
            // Get list of actions that match a given verb. This can match by name or IActionHttpMethodSelecto
            private ReflectedHttpActionDescriptor[] FindActionsForVerb(HttpMethod verb) {

                // Check cache for common verbs. 
                for (int i = 0; i < _cacheListVerbKinds.Length; i++) {

                    // verb selection on common verbs is normalized to have object reference identity. 
                    // This is significantly more efficient than comparing the verbs based on strings. 
                    if (object.ReferenceEquals(verb, _cacheListVerbKinds[i])) {

                        return _cacheListVerbs[i];
                    }
                }

                // General case for any verbs. 
                return FindActionsForVerbWorker(verb);
            }

            // This is called when we don't specify an Action name
            // Get list of actions that match a given verb. This can match by name or IActionHttpMethodSelector.
            // Since this list is fixed for a given verb type, it can be pre-computed and cached.   
            // This function should not do caching. It's the helper that builds the caches. 
            private ReflectedHttpActionDescriptor[] FindActionsForVerbWorker(HttpMethod verb) {

                List<ReflectedHttpActionDescriptor> listMethods = new List<ReflectedHttpActionDescriptor>();

                foreach (ReflectedHttpActionDescriptor descriptor in _actionDescriptors) {

                    if (descriptor.SupportedHttpMethods.Contains(verb)) {

                        listMethods.Add(descriptor);
                    }
                }

                return listMethods.ToArray();
            }

            private static string CreateAmbiguousMatchList(IEnumerable<HttpActionDescriptor> ambiguousDescriptors) {

                StringBuilder exceptionMessageBuilder = new StringBuilder();
                foreach (ReflectedHttpActionDescriptor descriptor in ambiguousDescriptors) {
                    MethodInfo methodInfo = descriptor.MethodInfo;

                    exceptionMessageBuilder.AppendLine();
                    exceptionMessageBuilder.Append(Error.Format(
                        SRResources.ActionSelector_AmbiguousMatchType,
                        methodInfo, methodInfo.DeclaringType.FullName));
                }

                return exceptionMessageBuilder.ToString();
            }

            private static bool IsValidActionMethod(MethodInfo methodInfo) {

                if (methodInfo.IsSpecialName) {

                    // not a normal method, e.g. a constructor or an event
                    return false;
                }

                if (methodInfo.GetBaseDefinition().DeclaringType.IsAssignableFrom(TypeHelper.ApiControllerType)) {

                    // is a method on Object, IHttpController, ApiController
                    return false;
                }

                return true;
            }
        }

        // We need to expose ILookup<string, HttpActionDescriptor>, but we have a ILookup<string, ReflectedHttpActionDescriptor>
        // ReflectedHttpActionDescriptor derives from HttpActionDescriptor, but ILookup doesn't support Covariance.
        // Adapter class since ILookup doesn't support Covariance. 
        // Fortunately, IGrouping, IEnumerable support Covariance, so it's easy to forward.
        private class LookupAdapter : ILookup<string, HttpActionDescriptor> {

            public ILookup<string, ReflectedHttpActionDescriptor> Source;

            public int Count {

                get { return Source.Count; }
            }

            public IEnumerable<HttpActionDescriptor> this[string key] {

                get { return Source[key]; }
            }

            public bool Contains(string key) {

                return Source.Contains(key);
            }

            public IEnumerator<IGrouping<string, HttpActionDescriptor>> GetEnumerator() {

                return Source.GetEnumerator();
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() {

                return Source.GetEnumerator();
            }
        }
    }
}