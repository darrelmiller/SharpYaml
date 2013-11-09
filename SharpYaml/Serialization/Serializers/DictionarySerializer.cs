// Copyright (c) 2013 SharpYaml - Alexandre Mutel
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
// 
// -------------------------------------------------------------------------------
// SharpYaml is a fork of YamlDotNet https://github.com/aaubry/YamlDotNet
// published with the following license:
// -------------------------------------------------------------------------------
// 
// Copyright (c) 2008, 2009, 2010, 2011, 2012 Antoine Aubry
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of
// this software and associated documentation files (the "Software"), to deal in
// the Software without restriction, including without limitation the rights to
// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
// of the Software, and to permit persons to whom the Software is furnished to do
// so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
using System;
using System.Collections.Generic;
using System.Linq;
using SharpYaml.Events;
using SharpYaml.Serialization.Descriptors;

namespace SharpYaml.Serialization.Serializers
{
	internal class DictionarySerializer : ObjectSerializer
	{
		public DictionarySerializer()
		{
		}

		public override IYamlSerializable TryCreate(SerializerContext context, ITypeDescriptor typeDescriptor)
		{
			return typeDescriptor is DictionaryDescriptor ? this : null;
		}

		protected override void ReadItem(SerializerContext context, object thisObject, ITypeDescriptor typeDescriptor)
		{
			var dictionaryDescriptor = (DictionaryDescriptor) typeDescriptor;

			if (dictionaryDescriptor.IsPureDictionary)
			{
				ReadPureDictionaryItems(context, thisObject, typeDescriptor);
			}
			else
			{
				var keyEvent = context.Reader.Peek<Scalar>();
				if (keyEvent != null)
				{
					if (keyEvent.Value == context.Settings.SpecialCollectionMember)
					{
						var reader = context.Reader;
						reader.Parser.MoveNext();

						reader.Expect<MappingStart>();
						ReadPureDictionaryItems(context, thisObject, typeDescriptor);
						reader.Expect<MappingEnd>();
						return;
					}
				}

                base.ReadItem(context, thisObject, typeDescriptor);	
			}
		}

		protected override void WriteItems(SerializerContext context, object thisObject, ITypeDescriptor typeDescriptor, YamlStyle style)
		{
			var dictionaryDescriptor = (DictionaryDescriptor)typeDescriptor;
			if (dictionaryDescriptor.IsPureDictionary)
			{
				WritePureDictionaryItems(context, thisObject, typeDescriptor);
			}
			else
			{
				// Serialize Dictionary members
				foreach (var member in typeDescriptor.Members)
				{
                    WriteMember(context, thisObject, typeDescriptor, style, member);
				}

                WriteMemberName(context, context.Settings.SpecialCollectionMember);

				context.Writer.Emit(new MappingStartEventInfo(thisObject, thisObject.GetType()) { Style = style });
				WritePureDictionaryItems(context, thisObject, typeDescriptor);
				context.Writer.Emit(new MappingEndEventInfo(thisObject, thisObject.GetType()));
			}
		}

		private void ReadPureDictionaryItems(SerializerContext context, object thisObject, ITypeDescriptor typeDescriptor)
		{
			var dictionaryDescriptor = (DictionaryDescriptor)typeDescriptor;

			var reader = context.Reader;
			while (!reader.Accept<MappingEnd>())
			{
                // Give a chance to pre-process keys and replace them on the fly
                // Pre-processing is only working on pure scalar keys (string, integers...etc)
			    var keyDecode = context.Reader.Peek<Scalar>();
			    bool isKeyDecoded = false;
			    string preKey = null;
			    if (keyDecode != null)
			    {
			        string newKey;
			        preKey = keyDecode.Value;
                    isKeyDecoded = context.DecodeKeyPre(thisObject, typeDescriptor, keyDecode.Value, out newKey);
			        keyDecode.Value = newKey;
			    }

                // Read key and value
				var keyResult = context.ReadYaml(null, dictionaryDescriptor.KeyType);
			    if (isKeyDecoded)
			    {
			        context.DecodeKeyPost(thisObject, typeDescriptor, keyResult.Value, preKey);
			    }

				var valueResult = context.ReadYaml(null, dictionaryDescriptor.ValueType);

				// Handle aliasing
				if (keyResult.IsAlias || valueResult.IsAlias)
				{
					if (keyResult.IsAlias)
					{
						if (valueResult.IsAlias)
						{
							context.AddAliasBinding(keyResult.Alias,
							                        deferredKey =>
							                        dictionaryDescriptor.AddToDictionary(thisObject, deferredKey,
							                                                             context.GetAliasValue(valueResult.Alias)));
						}
						else
						{
							context.AddAliasBinding(keyResult.Alias,
							                        deferredKey =>
							                        dictionaryDescriptor.AddToDictionary(thisObject, deferredKey, valueResult.Value));
						}
					}
					else
					{
						context.AddAliasBinding(valueResult.Alias,
						                        deferredAlias =>
						                        dictionaryDescriptor.AddToDictionary(thisObject, keyResult.Value, deferredAlias));
					}
				}
				else
				{
					dictionaryDescriptor.AddToDictionary(thisObject, keyResult.Value, valueResult.Value);
				}
			}
		}

		private void WritePureDictionaryItems(SerializerContext context, object thisObject, ITypeDescriptor typeDescriptor)
		{
			var dictionaryDescriptor = (DictionaryDescriptor)typeDescriptor;

			var keyValues = dictionaryDescriptor.GetEnumerator(thisObject).ToList();

			if (context.Settings.SortKeyForMapping)
			{
				keyValues.Sort(SortDictionaryByKeys);
			}

			var keyType = dictionaryDescriptor.KeyType;
			var valueType = dictionaryDescriptor.ValueType;

            // Allow to encode dictionary key before emitting them
		    Func<object, string, string> encodeScalarKey = context.KeyTransform != null ? (key, keyText) => context.EncodeKey(thisObject, typeDescriptor, key, keyText) : (Func<object, string,string>)null;

			foreach (var keyValue in keyValues)
			{
			    context.EncodeScalarKey = encodeScalarKey;
				context.WriteYaml(keyValue.Key, keyType);
				context.WriteYaml(keyValue.Value, valueType);
			}
		}

		private static int SortDictionaryByKeys(KeyValuePair<object, object> left, KeyValuePair<object, object> right)
		{
			if (left.Key is string && right.Key is string)
			{
				return string.CompareOrdinal((string)left.Key, (string)right.Key);
			}

			if (left.Key is IComparable && right.Key is IComparable)
			{
				return ((IComparable)left.Key).CompareTo(right.Key);
			}
			return 0;
		}
	}
}