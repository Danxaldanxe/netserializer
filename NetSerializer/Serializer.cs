﻿/*
 * Copyright 2015 Tomi Valkeinen
 * 
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Diagnostics;

namespace NetSerializer
{
	delegate void SerializeDelegate<T>(Serializer serializer, Stream stream, T ob);
	delegate void DeserializeDelegate<T>(Serializer serializer, Stream stream, out T ob);

	public class Serializer
	{
		readonly static ITypeSerializer[] s_typeSerializers = new ITypeSerializer[] {
			new ObjectSerializer(),
			new PrimitivesSerializer(),
			new ArraySerializer(),
			new EnumSerializer(),
			new DictionarySerializer(),
			new NullableSerializer(),
			new GenericSerializer(),
		};

		/// <summary>
		/// Initialize NetSerializer
		/// </summary>
		/// <param name="rootTypes">Types to be (de)serialized</param>
		public Serializer(IEnumerable<Type> rootTypes)
			: this(rootTypes, new ITypeSerializer[0])
		{
		}

		/// <summary>
		/// Initialize NetSerializer
		/// </summary>
		/// <param name="rootTypes">Types to be (de)serialized</param>
		/// <param name="userTypeSerializers">Array of custom serializers</param>
		public Serializer(IEnumerable<Type> rootTypes, ITypeSerializer[] userTypeSerializers)
		{
			if (userTypeSerializers.All(s => s is IDynamicTypeSerializer || s is IStaticTypeSerializer) == false)
				throw new ArgumentException("TypeSerializers have to implement IDynamicTypeSerializer or IStaticTypeSerializer");

			m_userTypeSerializers = userTypeSerializers;

			m_runtimeTypeMap = new Dictionary<Type, TypeData>();
			m_runtimeTypeIDMap = new Dictionary<ushort, TypeData>();

			InitializeTypeMaps(new[] { typeof(object) }.Concat(rootTypes));

			lock (m_modifyLock)
			{
				GenerateWriters(typeof(object));
				GenerateReaders(typeof(object));
			}
		}

		void InitializeTypeMaps(IEnumerable<Type> roots)
		{
			Stack<Type> stack = new Stack<Type>(roots);

			ushort typeID = 1;

			while (stack.Count > 0)
			{
				var type = stack.Pop();

				if (m_runtimeTypeMap.ContainsKey(type))
					continue;

				if (type.IsAbstract || type.IsInterface)
					continue;

				if (type.ContainsGenericParameters)
					throw new NotSupportedException(String.Format("Type {0} contains generic parameters", type.FullName));

				ITypeSerializer serializer = GetTypeSerializer(type);

				var data = new TypeData(type, typeID, serializer);
				m_runtimeTypeMap[type] = data;
				m_runtimeTypeIDMap[typeID] = data;
				typeID++;

				foreach (var t in serializer.GetSubtypes(type))
				{
					if (m_runtimeTypeMap.ContainsKey(t) == false)
						stack.Push(t);
				}
			}
		}



		readonly ITypeSerializer[] m_userTypeSerializers;

		readonly Dictionary<Type, TypeData> m_runtimeTypeMap;
		readonly Dictionary<ushort, TypeData> m_runtimeTypeIDMap;

		readonly object m_modifyLock = new object();

		[Conditional("DEBUG")]
		void AssertLocked()
		{
			Debug.Assert(System.Threading.Monitor.IsEntered(m_modifyLock));
		}

		public void Serialize(Stream stream, object ob)
		{
			ObjectSerializer.Serialize(this, stream, ob);
		}

		public object Deserialize(Stream stream)
		{
			object ob;
			ObjectSerializer.Deserialize(this, stream, out ob);
			return ob;
		}

		public void Deserialize(Stream stream, out object ob)
		{
			ObjectSerializer.Deserialize(this, stream, out ob);
		}

		public void SerializeDirect<T>(Stream stream, T value)
		{
			var del = (SerializeDelegate<T>)m_runtimeTypeMap[typeof(T)].WriterDirectDelegate;

			if (del == null)
			{
				lock (m_modifyLock)
					del = (SerializeDelegate<T>)GenerateDirectWriterDelegate(typeof(T));
			}

			del(this, stream, value);
		}

		public void DeserializeDirect<T>(Stream stream, out T value)
		{
			var del = (DeserializeDelegate<T>)m_runtimeTypeMap[typeof(T)].ReaderDirectDelegate;

			if (del == null)
			{
				lock (m_modifyLock)
					del = (DeserializeDelegate<T>)GenerateDirectReaderDelegate(typeof(T));
			}

			del(this, stream, out value);
		}

		internal ushort GetTypeIdAndSerializer(Type type, out SerializeDelegate<object> del)
		{
			var data = m_runtimeTypeMap[type];

			if (data.WriterTrampolineDelegate != null)
			{
				del = data.WriterTrampolineDelegate;
				return data.TypeID;
			}

			lock (m_modifyLock)
			{
				del = GenerateWriterTrampoline(type);
				return data.TypeID;
			}
		}

		internal DeserializeDelegate<object> GetDeserializeTrampolineFromId(ushort id)
		{
			var data = m_runtimeTypeIDMap[id];

			if (data.ReaderTrampolineDelegate != null)
				return data.ReaderTrampolineDelegate;

			lock (m_modifyLock)
			{
				return GenerateReaderTrampoline(data.Type);
			}
		}

		ITypeSerializer GetTypeSerializer(Type type)
		{
			var serializer = m_userTypeSerializers.FirstOrDefault(h => h.Handles(type));

			if (serializer == null)
				serializer = s_typeSerializers.FirstOrDefault(h => h.Handles(type));

			if (serializer == null)
				throw new NotSupportedException(String.Format("No serializer for {0}", type.FullName));

			return serializer;
		}

		internal TypeData GetIndirectData(Type type)
		{
			TypeData data;

			if (!m_runtimeTypeMap.TryGetValue(type, out data) || data.CanCallDirect == false)
				return m_runtimeTypeMap[typeof(object)];

			return data;
		}

		internal MethodInfo GetDirectWriter(Type type)
		{
			return m_runtimeTypeMap[type].WriterMethodInfo;
		}

		internal MethodInfo GetDirectReader(Type type)
		{
			return m_runtimeTypeMap[type].ReaderMethodInfo;
		}


		HashSet<Type> Collect(Type rootType)
		{
			var l = new HashSet<Type>();
			Stack<Type> stack = new Stack<Type>();

			stack.Push(rootType);

			while (stack.Count > 0)
			{
				var type = stack.Pop();

				if (type.IsAbstract || type.IsInterface)
					continue;

				if (type.ContainsGenericParameters)
					throw new NotSupportedException(String.Format("Type {0} contains generic parameters", type.FullName));

				ITypeSerializer serializer = m_runtimeTypeMap[type].TypeSerializer;

				foreach (var t in serializer.GetSubtypes(type))
				{
					if (l.Contains(t) == false)
						stack.Push(t);
				}

				l.Add(type);
			}

			return l;
		}

		void GenerateWriterStub(Type type)
		{
			AssertLocked();

			var data = m_runtimeTypeMap[type];

			ITypeSerializer serializer = data.TypeSerializer;

			MethodInfo writer;

			if (serializer is IStaticTypeSerializer)
			{
				var sts = (IStaticTypeSerializer)serializer;

				writer = sts.GetStaticWriter(type);

				Debug.Assert(writer != null);
			}
			else if (serializer is IDynamicTypeSerializer)
			{
				// TODO: make it possible for dyn serializers to not have Serializer param
				writer = Helpers.GenerateDynamicSerializerStub(type);
			}
			else
			{
				throw new Exception();
			}

			data.WriterMethodInfo = writer;
		}

		void GenerateWriterBody(Type type)
		{
			AssertLocked();

			var data = m_runtimeTypeMap[type];

			ITypeSerializer serializer = data.TypeSerializer;

			var writer = data.WriterMethodInfo as DynamicMethod;
			if (writer == null)
				return;

			var dynSer = (IDynamicTypeSerializer)serializer;

			dynSer.GenerateWriterMethod(this, type, writer.GetILGenerator());
		}

		void GenerateWriters(Type rootType)
		{
			AssertLocked();

			if (m_runtimeTypeMap[rootType].WriterMethodInfo != null)
				return;

			List<Type> types = Collect(rootType).Where(t => m_runtimeTypeMap[t].WriterMethodInfo == null).ToList();

			foreach (var type in types)
				GenerateWriterStub(type);

			foreach (var type in types)
				GenerateWriterBody(type);
		}

		SerializeDelegate<object> GenerateWriterTrampoline(Type type)
		{
			AssertLocked();

			var data = m_runtimeTypeMap[type];

			if (data.WriterTrampolineDelegate != null)
				return data.WriterTrampolineDelegate;

			GenerateWriters(type);

			data.WriterTrampolineDelegate = (SerializeDelegate<object>)Helpers.CreateSerializeDelegate(typeof(object), data);
			return data.WriterTrampolineDelegate;
		}

		Delegate GenerateDirectWriterDelegate(Type type)
		{
			AssertLocked();

			var data = m_runtimeTypeMap[type];

			if (data.WriterDirectDelegate != null)
				return data.WriterDirectDelegate;

			GenerateWriters(type);

			data.WriterDirectDelegate = Helpers.CreateSerializeDelegate(type, data);
			return data.WriterDirectDelegate;
		}



		void GenerateReaderStub(Type type)
		{
			AssertLocked();

			var data = m_runtimeTypeMap[type];

			ITypeSerializer serializer = data.TypeSerializer;

			MethodInfo reader;

			if (serializer is IStaticTypeSerializer)
			{
				var sts = (IStaticTypeSerializer)serializer;

				reader = sts.GetStaticReader(type);

				Debug.Assert(reader != null);
			}
			else if (serializer is IDynamicTypeSerializer)
			{
				// TODO: make it possible for dyn serializers to not have Serializer param
				reader = Helpers.GenerateDynamicDeserializerStub(type);
			}
			else
			{
				throw new Exception();
			}

			data.ReaderMethodInfo = reader;
		}

		void GenerateReaderBody(Type type)
		{
			AssertLocked();

			var data = m_runtimeTypeMap[type];

			ITypeSerializer serializer = data.TypeSerializer;

			var reader = data.ReaderMethodInfo as DynamicMethod;
			if (reader == null)
				return;

			var dynSer = (IDynamicTypeSerializer)serializer;

			dynSer.GenerateReaderMethod(this, type, reader.GetILGenerator());
		}

		void GenerateReaders(Type rootType)
		{
			AssertLocked();

			if (m_runtimeTypeMap[rootType].ReaderMethodInfo != null)
				return;

			List<Type> types = Collect(rootType).Where(t => m_runtimeTypeMap[t].ReaderMethodInfo == null).ToList();

			foreach (var type in types)
				GenerateReaderStub(type);

			foreach (var type in types)
				GenerateReaderBody(type);
		}

		DeserializeDelegate<object> GenerateReaderTrampoline(Type type)
		{
			AssertLocked();

			var data = m_runtimeTypeMap[type];

			if (data.ReaderTrampolineDelegate != null)
				return data.ReaderTrampolineDelegate;

			GenerateReaders(type);

			data.ReaderTrampolineDelegate = (DeserializeDelegate<object>)Helpers.CreateDeserializeDelegate(typeof(object), data);
			return data.ReaderTrampolineDelegate;
		}

		Delegate GenerateDirectReaderDelegate(Type type)
		{
			AssertLocked();

			var data = m_runtimeTypeMap[type];

			if (data.ReaderDirectDelegate != null)
				return data.ReaderDirectDelegate;

			GenerateReaders(type);

			data.ReaderDirectDelegate = Helpers.CreateDeserializeDelegate(type, data);
			return data.ReaderDirectDelegate;
		}



#if GENERATE_DEBUGGING_ASSEMBLY

		public static void GenerateDebugAssembly(IEnumerable<Type> rootTypes, ITypeSerializer[] userTypeSerializers)
		{
			new Serializer(rootTypes, userTypeSerializers, true);
		}

		Serializer(IEnumerable<Type> rootTypes, ITypeSerializer[] userTypeSerializers, bool debugAssembly)
		{
			if (userTypeSerializers.All(s => s is IDynamicTypeSerializer || s is IStaticTypeSerializer) == false)
				throw new ArgumentException("TypeSerializers have to implement IDynamicTypeSerializer or  IStaticTypeSerializer");

			m_userTypeSerializers = userTypeSerializers;

			var ab = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("NetSerializerDebug"), AssemblyBuilderAccess.RunAndSave);
			var modb = ab.DefineDynamicModule("NetSerializerDebug.dll");
			var tb = modb.DefineType("NetSerializer", TypeAttributes.Public);

			m_runtimeTypeMap = new Dictionary<Type, TypeData>();
			m_runtimeTypeIDMap = new Dictionary<ushort, TypeData>();

			InitializeTypeMaps(new[] { typeof(object) }.Concat(rootTypes));

			/* generate stubs */
			foreach (var type in m_runtimeTypeMap.Keys)
				GenerateDebugStubs(type, tb);

			foreach (var type in m_runtimeTypeMap.Keys)
				GenerateDebugBodies(type);

			tb.CreateType();
			ab.Save("NetSerializerDebug.dll");
		}

		void GenerateDebugStubs(Type type, TypeBuilder tb)
		{
			var data = m_runtimeTypeMap[type];

			ITypeSerializer serializer = data.TypeSerializer;

			MethodInfo writer;
			MethodInfo reader;
			bool writerNeedsInstance, readerNeedsInstance;

			if (serializer is IStaticTypeSerializer)
			{
				var sts = (IStaticTypeSerializer)serializer;

				writer = sts.GetStaticWriter(type);
				reader = sts.GetStaticReader(type);

				writerNeedsInstance = writer.GetParameters().Length == 3;
				readerNeedsInstance = reader.GetParameters().Length == 3;
			}
			else if (serializer is IDynamicTypeSerializer)
			{
				writer = Helpers.GenerateStaticSerializerStub(tb, type);
				reader = Helpers.GenerateStaticDeserializerStub(tb, type);

				writerNeedsInstance = readerNeedsInstance = true;
			}
			else
			{
				throw new Exception();
			}

			data.WriterMethodInfo = writer;
			data.WriterNeedsInstanceDebug = writerNeedsInstance;

			data.ReaderMethodInfo = reader;
			data.ReaderNeedsInstanceDebug = readerNeedsInstance;
		}

		void GenerateDebugBodies(Type type)
		{
			var data = m_runtimeTypeMap[type];

			ITypeSerializer serializer = data.TypeSerializer;

			var dynSer = serializer as IDynamicTypeSerializer;
			if (dynSer == null)
				return;

			var writer = data.WriterMethodInfo as MethodBuilder;
			if (writer == null)
				throw new Exception();

			var reader = data.ReaderMethodInfo as MethodBuilder;
			if (reader == null)
				throw new Exception();

			dynSer.GenerateWriterMethod(this, type, writer.GetILGenerator());
			dynSer.GenerateReaderMethod(this, type, reader.GetILGenerator());
		}
#endif
	}
}
