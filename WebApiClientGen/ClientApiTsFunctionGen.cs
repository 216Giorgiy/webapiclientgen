﻿using System;
using System.CodeDom;
using System.Linq;
using System.Collections.ObjectModel;
using System.Collections.Generic;

using System.Web.Http;
using System.Web.Http.Description;
using System.Diagnostics;
using System.Text;
using Fonlow.TypeScriptCodeDom;

namespace Fonlow.CodeDom.Web.Ts
{
    /// <summary>
    /// Generate a client function upon ApiDescription
    /// </summary>
    internal class ClientApiTsFunctionGen
    {
        SharedContext sharedContext;
        ApiDescription description;
        string relativePath;
        //  string route;
        Collection<ApiParameterDescription> parameterDescriptions;
        string controllerName;
        string methodName;
        Type returnType;
        CodeMemberMethod method;

        public ClientApiTsFunctionGen(SharedContext sharedContext, ApiDescription description)
        {
            this.description = description;
            this.sharedContext = sharedContext;

            relativePath = description.RelativePath;
            parameterDescriptions = description.ParameterDescriptions;
            controllerName = description.ActionDescriptor.ControllerDescriptor.ControllerName;


            methodName = description.ActionDescriptor.ActionName;
            if (methodName.EndsWith("Async"))
                methodName = methodName.Substring(0, methodName.Length - 5);

            returnType = description.ActionDescriptor.ReturnType;

        }

        static readonly Type typeOfHttpActionResult = typeof(System.Web.Http.IHttpActionResult);
        static readonly Type typeOfChar = typeof(char);
        static readonly Type typeOfString = typeof(string);

        public static CodeMemberMethod Create(SharedContext sharedContext, ApiDescription description)
        {
            var gen = new ClientApiTsFunctionGen(sharedContext, description);
            return gen.CreateApiFunction();
        }

        public CodeMemberMethod CreateApiFunction()
        {
            //create method
            method = CreateMethodName();

            //    var returnTypeReference = method.ReturnType;

            CreateDocComments();


            //  var binderAttributes = description.ParameterDescriptions.Select(d => d.ParameterDescriptor.ParameterBinderAttribute).ToArray();

            switch (description.HttpMethod.Method)
            {
                case "GET":
                    RenderGetOrDeleteImplementation("get");
                    break;
                case "DELETE":
                    RenderGetOrDeleteImplementation("delete");
                    break;
                case "POST":
                    RenderPostOrPutImplementation("post");
                    break;
                case "PUT":
                    RenderPostOrPutImplementation("put");
                    break;

                default:
                    Trace.TraceWarning("This HTTP method {0} is not yet supported", description.HttpMethod.Method);
                    break;
            }

            return method;
        }

        void CreateDocComments()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine(description.Documentation);
            builder.AppendLine(description.HttpMethod.Method + " " + description.RelativePath);
            foreach (var item in description.ParameterDescriptions)
            {
                var parameterType = TranslateCustomTypeToClientType(item.ParameterDescriptor.ParameterType);
                builder.AppendLine($"@param {{{parameterType}}} {item.Name} {item.Documentation}");
            }

            var returnType = description.ResponseDescription.ResponseType == null ? "void" : TranslateCustomTypeToClientType(description.ResponseDescription.ResponseType);
            builder.AppendLine($"@return {{{returnType}}} {description.ResponseDescription.Documentation}");
            method.Comments.Add(new CodeCommentStatement(builder.ToString(), true));
        }


        CodeMemberMethod CreateMethodName()
        {
            return new CodeMemberMethod()
            {
                Attributes = MemberAttributes.Public | MemberAttributes.Final,
                Name = methodName,
                //  ReturnType = returnType == null ? null : new CodeTypeReference(TranslateCustomTypeToClientType(returnType)),
            };
        }


        string GetGenericTypeFriendlyName(Type r)
        {
            var separatorPosition = r.Name.IndexOf("`");
            var genericTypeName = r.Name.Substring(0, separatorPosition);
            var typeNameList = r.GenericTypeArguments.Select(d => TranslateCustomTypeToClientType(d));//support only 1 level of generic. This should be good enough. If more needed, recursive algorithm will help
            var typesLiteral = String.Join(", ", typeNameList);
            return String.Format("{0}<{1}>", genericTypeName, typesLiteral);
        }

        string TranslateCustomTypeToClientType(Type t)
        {
            if (t == null)
                return null;

            if (sharedContext.prefixesOfCustomNamespaces.Any(d => t.Namespace.StartsWith(d)))
                return t.Namespace.Replace('.', '_') + "_Client." + t.Name;//The alias name in TS import

            var r = TypeMapper.GetCodeTypeReferenceText(new CodeTypeReference(t));
            if (r != "any")
                return r;
            //if (t == typeOfHttpActionResult)
            //    return "System.Net.Http.HttpResponseMessage";

            return t.FullName;
        }

        static string RefineCustomComplexTypeText(Type t)
        {
            return t.Namespace.Replace('.', '_') + "_Client." + t.Name;
        }

        static bool IsClassOrStruct(Type type)
        {
            return type.IsClass || (type.IsValueType && !type.IsPrimitive && !type.IsEnum);
        }

        void RenderGetOrDeleteImplementation(string httpMethod)
        {
            //Create function parameters
            var parameters = description.ParameterDescriptions.Select(d => new CodeParameterDeclarationExpression()
            {
                Name = d.Name,
                Type = new CodeTypeReference(TranslateCustomTypeToClientType(d.ParameterDescriptor.ParameterType)),

            }).ToList();

            var callbackTypeText = $"(data : {TranslateCustomTypeToClientType(returnType)}) = > any";
            parameters.Add(new CodeParameterDeclarationExpression()
            {
                Name = "callback",
                Type = new CodeTypeReference(callbackTypeText),
            });

            method.Parameters.AddRange(parameters.ToArray());

            var jsUriQuery = CreateUriQuery(description.RelativePath, description.ParameterDescriptions);
            var uriText = jsUriQuery == null ? $"'{description.RelativePath}'" : RemoveTrialEmptyString( $"encodeURI('{jsUriQuery}')");
            method.Statements.Add(new CodeSnippetStatement(
                $"this.httpClient.{httpMethod}({uriText}, callback, this.error, this.statusCode);"));
        }

        void RenderPostOrPutImplementation(string httpMethod)
        {
            //Create function parameters
            var parameters = description.ParameterDescriptions.Select(d => new CodeParameterDeclarationExpression()
            {
                Name = d.Name,
                Type = new CodeTypeReference(TranslateCustomTypeToClientType(d.ParameterDescriptor.ParameterType)),

            }).ToList();

            var fromBodyParameterDescriptions = description.ParameterDescriptions.Where(d => d.ParameterDescriptor.ParameterBinderAttribute is FromBodyAttribute
                || (IsComplexType(d.ParameterDescriptor.ParameterType) && (!(d.ParameterDescriptor.ParameterBinderAttribute is FromUriAttribute) 
                || (d.ParameterDescriptor.ParameterBinderAttribute == null)))).ToArray();
            if (fromBodyParameterDescriptions.Length > 1)
            {
                throw new InvalidOperationException(String.Format("This API function {0} has more than 1 FromBody bindings in parameters", description.ActionDescriptor.ActionName));
            }
            var singleFromBodyParameterDescription = fromBodyParameterDescriptions.FirstOrDefault();

            var callbackTypeText = $"(data : {TranslateCustomTypeToClientType(returnType)}) = > any";
            parameters.Add(new CodeParameterDeclarationExpression()
            {
                Name = "callback",
                Type = new CodeTypeReference(callbackTypeText),
            });

            method.Parameters.AddRange(parameters.ToArray());

            var dataToPost = singleFromBodyParameterDescription == null ? "null" : singleFromBodyParameterDescription.ParameterDescriptor.ParameterName;

            var jsUriQuery = CreateUriQuery(description.RelativePath, description.ParameterDescriptions);
            var uriText = jsUriQuery == null ? $"'{description.RelativePath}'" : RemoveTrialEmptyString( $"encodeURI('{jsUriQuery}')");

            method.Statements.Add(new CodeSnippetStatement(
                $"this.httpClient.{httpMethod}({uriText}, {dataToPost}, callback, this.error, this.statusCode);"));
        }

        static string RemoveTrialEmptyString(string s)
        {
            var p = s.IndexOf("+''");
            return s.Remove(p, 3);
        }


        static string CreateUriQuery(string uriText, Collection<ApiParameterDescription> parameterDescriptions)
        {
            var template = new UriTemplate(uriText);

            if (template.QueryValueVariableNames.Count == 0 && template.PathSegmentVariableNames.Count == 0)
                return null;

            string newUriText = uriText;

            var hasQuery = template.QueryValueVariableNames.Count > 0;

            for (int i = 0; i < template.PathSegmentVariableNames.Count; i++)
            {
                var name = template.PathSegmentVariableNames[i];//PathSegmentVariableNames[i] always give uppercase
                var d = parameterDescriptions.FirstOrDefault(r => r.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
                Debug.Assert(d != null);
                newUriText = newUriText.Replace($"{{{d.Name}}}", $"'+{d.Name}+'");
            }

            for (int i = 0; i < template.QueryValueVariableNames.Count; i++)
            {
                var name = template.QueryValueVariableNames[i];
                var d = parameterDescriptions.FirstOrDefault(r => r.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
                Debug.Assert(d != null);
                newUriText = newUriText.Replace($"{{{d.Name}}}", $"'+{d.Name}+'");
            }

            return newUriText;
        }

        static readonly Type typeOfHttpResponseMessage = typeof(System.Net.Http.HttpResponseMessage);



        bool IsSimpleType(Type type)
        {
            return type.IsPrimitive || type.Equals(typeOfString);
        }

        bool IsComplexType(Type type)
        {
            return !IsSimpleType(type);
        }

        bool IsStringType(Type type)
        {
            return type.Equals(typeOfString);
        }


    }

}
