﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hl7.Fhir.Introspection;
using Hl7.Fhir.Model;
using Hl7.Fhir.Validation;

namespace Fhir.Anonymizer.Core.AnonymizerConfigurations.Validation
{
    public class FhirSchemaProvider
    {
        private const string NamedBackBoneElementSuffix = "Component";
        private HashSet<string> _resourceNameSet = new HashSet<string>();
        private HashSet<string> _typeNameSet = new HashSet<string>();
        private HashSet<string> _namedBackboneElementSet = new HashSet<string>();
        private Dictionary<string, FhirTypeNode> _fhirSchema = new Dictionary<string, FhirTypeNode>();
        public FhirSchemaProvider()
        {
            foreach (var type in GetTypesWithCustomAttribute(typeof(FhirTypeAttribute)))
            {
                var typeAttribute = type.GetCustomAttribute<FhirTypeAttribute>();
                var typeNameKey = GetTypeNameKey(type);

                if (_fhirSchema.ContainsKey(typeNameKey))
                {
                    continue;
                }

                if (typeAttribute.IsResource)
                {
                    _resourceNameSet.Add(typeNameKey);
                }
                // Ignore backbone element in data type set
                else if(!typeAttribute.NamedBackboneElement)
                {
                    _typeNameSet.Add(typeNameKey);
                }
                else
                {
                    _namedBackboneElementSet.Add(typeNameKey);
                }

                var typeNode = new FhirTypeNode
                {
                    InstanceType = typeNameKey,
                    Name = string.Empty,
                    IsResource = typeAttribute.IsResource,
                };
                var childrens = new Dictionary<string, IEnumerable<FhirTypeNode>>();
                
                // Resolve properties for non-Primitive types
                if (!IsPrimitiveType(type))
                {
                    var properties = type.GetProperties();
                    foreach (var property in properties)
                    {
                        var elementAttribute = property.GetCustomAttributes<FhirElementAttribute>().FirstOrDefault();
                        if (elementAttribute != null)
                        {
                            var fieldTypes = new List<Type>();
                            // Add all allowed types for Choice Element property
                            if (elementAttribute.Choice != ChoiceType.None)
                            {
                                var allowedTypeAttribute = property.GetCustomAttributes<AllowedTypesAttribute>().FirstOrDefault();
                                fieldTypes.AddRange(allowedTypeAttribute.Types);
                            }
                            // Some elements (e.g. Extension.url) have ImplementingType = string, but FhirType = FhirUri, etc.
                            else if (elementAttribute.TypeRedirect != null) 
                            {
                                fieldTypes.Add(elementAttribute.TypeRedirect);
                            }
                            else
                            {
                                fieldTypes.Add(property.PropertyType);
                            }

                            var nodes = fieldTypes.Select(type =>
                                new FhirTypeNode
                                {
                                    InstanceType = GetTypeNameKey(type),
                                    Name = elementAttribute.Name,
                                    IsResource = elementAttribute.Choice == ChoiceType.ResourceChoice,
                                    Parent = typeNode
                                });
                            childrens.Add(elementAttribute.Name, nodes);
                        }
                    }
                }
                typeNode.Children = childrens;
                _fhirSchema.Add(typeNameKey, typeNode);
            }
        }

        public RuleValidationResult ValidateRule(string path, string method, AnonymizerRuleType type, HashSet<string> methodSupportedFieldTypes)
        {
            var pathComponents = path.Split('.', StringSplitOptions.None);
            if (!pathComponents.Any() || pathComponents.Where(string.IsNullOrEmpty).Any())
            {
                return new RuleValidationResult
                {
                    Success = false,
                    ErrorMessage = $"{path} is invalid."
                };
            }

            var currentTypeName = pathComponents.First();
            // Type rules start with data type
            if (type == AnonymizerRuleType.TypeRule) 
            {
                if (!_typeNameSet.Contains(currentTypeName))
                {
                    return new RuleValidationResult
                    {
                        Success = false,
                        ErrorMessage = $"{currentTypeName} is an invalid data type."
                    };
                }
                else if (currentTypeName.Equals("BackboneElement"))
                {
                    return new RuleValidationResult
                    {
                        Success = false,
                        ErrorMessage = $"{currentTypeName} is a valid but not supported data type."
                    };
                }
            }
            // Path rules start with resource type
            else
            {
                if (!_resourceNameSet.Contains(currentTypeName))
                {
                    return new RuleValidationResult
                    {
                        Success = false,
                        ErrorMessage = $"{currentTypeName} is an invalid resource type."
                    };
                }
                else if (path.StartsWith("Bundle.entry") || path.StartsWith($"{currentTypeName}.contained"))
                {
                    return new RuleValidationResult
                    {
                        Success = false,
                        ErrorMessage = $"Path of Bundle/contained resources is not supported."
                    };
                }
            }

            var pathValidationResult = ValidateRulePathComponents(pathComponents.ToList(), 1, currentTypeName);
            if (!pathValidationResult.Success)
            {
                return pathValidationResult;
            }

            if (!Enum.TryParse<AnonymizerMethod>(method, true, out _))
            {
                return new RuleValidationResult
                {
                    Success = false,
                    ErrorMessage = $"Anonymization method {method} is currently not supported."
                };
            }

            if (methodSupportedFieldTypes != null && !methodSupportedFieldTypes.Contains(pathValidationResult.TargetDataType)) 
            {
                return new RuleValidationResult
                {
                    Success = false,
                    ErrorMessage = $"Anonymization method {method} cannot be applied to {string.Join('.', pathComponents)}."
                };
            }

            return new RuleValidationResult
            {
                Success = true,
                TargetDataType = pathValidationResult.TargetDataType
            };
        }

        public HashSet<string> GetFhirResourceTypes()
        {
            return _resourceNameSet;
        }

        public HashSet<string> GetFhirDataTypes()
        {
            return _typeNameSet;
        }

        public HashSet<string> GetFhirAllTypes()
        {
            return _resourceNameSet.Union(_typeNameSet).Union(_namedBackboneElementSet).ToHashSet();
        }

        public Dictionary<string, FhirTypeNode> GetFhirSchema()
        {
            return _fhirSchema;
        }

        private RuleValidationResult ValidateRulePathComponents(List<string> pathComponents, int index, string typeName)
        {
            if (index >= pathComponents.Count())
            {
                return new RuleValidationResult
                {
                    Success = true,
                    TargetDataType = typeName
                };
            }

            var typeSchema = _fhirSchema.GetValueOrDefault(typeName);
            if (typeSchema == null)
            {
                return new RuleValidationResult
                {
                    Success = false,
                    ErrorMessage = $"{typeName} is an invalid data type."
                };
            }

            var fieldName = pathComponents[index];
            if (!typeSchema.Children.ContainsKey(fieldName))
            {
                return new RuleValidationResult
                {
                    Success = false,
                    ErrorMessage = $"{fieldName} is an invalid field in {string.Join('.', pathComponents.Take(index))}."
                };

            }

            string errorMessage = string.Empty;
            foreach(var node in typeSchema.Children[fieldName])
            {
                var result = ValidateRulePathComponents(pathComponents, index + 1, node.InstanceType);
                if (result.Success)
                {
                    return result;
                }

                errorMessage = result.ErrorMessage;
            }

            return new RuleValidationResult
            {
                Success = false,
                ErrorMessage = errorMessage
            };
        }

        private bool IsPrimitiveType(Type type)
        {
            if (typeof(Primitive).IsAssignableFrom(type))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Resolve all types from assembly with a attribute
        /// </summary>
        /// <param name="attributeType"></param>
        /// <returns></returns>
        private IEnumerable<Type> GetTypesWithCustomAttribute(Type attributeType)
        {
            var assembly = attributeType.Assembly;
            foreach (Type type in assembly.GetTypes())
            {
                if (type.GetCustomAttributes(attributeType, false).Length > 0)
                {
                    yield return type;
                }
            }
        }

        /// <summary>
        /// Get key for a FhirType object, return an alias of "ResourceName_FieldName" for a BackboneElement type.
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        private string GetTypeNameKey(Type type)
        {
            var currentType = type;

            // Transform "CodeOfT" to "code"
            if  (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Code<>))
            {
                return "code";
            }
            // Unwrap actual type from List<T>
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                currentType = type.GetGenericArguments().First();
            }

            var typeAttribute = currentType.GetCustomAttribute<FhirTypeAttribute>();
            var typeName = typeAttribute.Name;
            if (string.Equals(typeName, "codeOfT"))
            {
                typeName = "code";
            }

            if (!typeAttribute.NamedBackboneElement)
            {
                return typeName;
            }
            else
            {
                var resourceType = currentType.DeclaringType;
                if (resourceType != null)
                {
                    var resourceAttribute = resourceType.GetCustomAttribute<FhirTypeAttribute>();
                    // Resolve fieldName for NamedBackboneElement Type, i.e. ItemComponent => "item"
                    if (typeName.Length > NamedBackBoneElementSuffix.Length)
                    {
                        var fieldName = typeAttribute.Name.Substring(0, typeName.Length - NamedBackBoneElementSuffix.Length).ToLower();
                        return $"{resourceAttribute.Name}_{fieldName}";
                    }
                }
                return typeName;
            }
        }

    }
}