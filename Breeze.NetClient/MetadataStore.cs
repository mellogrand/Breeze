﻿using Breeze.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Breeze.NetClient {

  // This class is ThreadSafe
  // and every object returned by it is immutable after being associated with this class.

  public class MetadataStore : IJsonSerializable {

    #region Ctor related 

    private MetadataStore() {
      _clrTypeMap = new ClrTypeMap(this);
      RegisterTypeDiscoveryActionCore(typeof(IEntity), (t) => _clrTypeMap.GetStructuralType(t), false);
      RegisterTypeDiscoveryActionCore(typeof(IComplexObject), (t) => _clrTypeMap.GetStructuralType(t), false);
      RegisterTypeDiscoveryActionCore(typeof(Validator), (t) => RegisterValidator(t), true);
    }

    private bool NotThisAssembly(Assembly assembly) {
      return (assembly != this.GetType().GetTypeInfo().Assembly);
    }

     // Explicit static constructor to tell C# compiler
    // not to mark type as beforefieldinit
    static MetadataStore() {     }

    public static MetadataStore Instance {
      get {
        return __instance;
      }
    }

    #endregion

    #region Public properties

    public static String MetadataVersion = "1.0.3";

    public List<EntityType> EntityTypes {
      get {
        lock (_structuralTypes) {
          return _structuralTypes.OfType<EntityType>().ToList();
        }
      }
    }

    public List<ComplexType> ComplexTypes {
      get {
        lock (_structuralTypes) {
          return _structuralTypes.OfType<ComplexType>().ToList();
        }
      }
    }

    public NamingConvention NamingConvention {
      get { 
        lock( _lock) {
          return _namingConvention;
        }
      }
      set {
        lock (_lock) {
          _namingConvention = value;
        }
      } 
    }

    public IEnumerable<Exception> Errors {
      get { return _errors.ToList(); }
    }

    #endregion

    #region Public methods

    public static void __Reset() {
      lock (__lock) {
        var x = __instance._probedAssemblies;
        __instance = new MetadataStore();
        __instance.ProbeAssemblies(x.ToArray());
      }
    }

    public bool ProbeAssemblies(params Assembly[] assembliesToProbe) {
      lock (_structuralTypes) {
        var assemblies = assembliesToProbe.Except(_probedAssemblies).ToList();
        if (assemblies.Any()) {
          assemblies.ForEach(asm => {
            _probedAssemblies.Add(asm);
            _typeDiscoveryActions.Where(tpl => tpl.Item3 == null || tpl.Item3(asm))
              .ForEach(tpl => {
                var type = tpl.Item1;
                var action = tpl.Item2;
                TypeFns.GetTypesImplementing(type, asm).ForEach(t => action(t));
              });
          });
          return true;
        } else {
          return false;
        }
      }
    }

    public void RegisterTypeDiscoveryAction(Type type, Action<Type> action) {
      RegisterTypeDiscoveryActionCore(type, action, false);
    }

    private void RegisterTypeDiscoveryActionCore(Type type, Action<Type> action, bool includeThisAssembly) {
      Func<Assembly, bool> shouldProcessAssembly = (a) => {
        return includeThisAssembly ? true : a != this.GetType().GetTypeInfo().Assembly;
      };
      _typeDiscoveryActions.Add(Tuple.Create(type, action, shouldProcessAssembly));
    }

    public void RegisterTypeInitializer(Type type, Action<Object> action) {
      lock (_typeInitializerMap) {
        if (action!=null) {
          _typeInitializerMap[type] = action;
        } else {
          if (_typeInitializerMap.ContainsKey(type)) {
            _typeInitializerMap.Remove(type);
          }
        }
      }
    }

    public async Task<DataService> FetchMetadata(DataService dataService) {
      String serviceName;
      
      serviceName = dataService.ServiceName;
      var ds = GetDataService(serviceName);
      if (ds != null) return dataService;

      await _asyncSemaphore.WaitAsync();

      try {
        ds = GetDataService(serviceName);
        if (ds != null) return dataService;

        var metadata = await dataService.GetAsync("Metadata");
        dataService.ServerMetadata = metadata;
        lock (_dataServiceMap) {
          _dataServiceMap[serviceName] = dataService;
        }
        var metadataProcessor = new CsdlMetadataProcessor(this, metadata);

        return dataService;

      } finally {
        _asyncSemaphore.Release();
      }

    }

    public DataService GetDataService(String serviceName) {
      lock (_dataServiceMap) {
        if (_dataServiceMap.ContainsKey(serviceName)) {
          return _dataServiceMap[serviceName];
        } else {
          return null;
        }
      }
    }

    public EntityType GetEntityType(Type clrEntityType, bool okIfNotFound = false) {
      return GetStructuralType<EntityType>(clrEntityType, okIfNotFound);
    }

    public EntityType GetEntityType(String etName, bool okIfNotFound = false) {
      return GetStructuralType<EntityType>(etName, okIfNotFound);
    }

    public ComplexType GetComplexType(Type clrComplexType, bool okIfNotFound = false) {
      return GetStructuralType<ComplexType>(clrComplexType, okIfNotFound);
    }

    public ComplexType GetComplexType(String ctName, bool okIfNotFound = false) {
      return GetStructuralType<ComplexType>(ctName, okIfNotFound);
    }

    public StructuralType GetStructuralType(Type clrType, bool okIfNotFound = false) {
      lock (_structuralTypes) {
        if (IsStructuralType(clrType)) {
          var stType = _clrTypeMap.GetStructuralType(clrType);
          if (stType != null) return stType;

          // Not sure if this is needed.
          if (ProbeAssemblies(new Assembly[] { clrType.GetTypeInfo().Assembly })) {
            stType = _clrTypeMap.GetStructuralType(clrType);
            if (stType != null) return stType;
          }
        }

        if (okIfNotFound) return null;
        throw new Exception("Unable to find a matching EntityType or ComplexType for " + clrType.Name);
      }
    }

    public EntityType AddEntityType(EntityType entityType) {
      if (entityType.KeyProperties.Count() == 0 && !entityType.IsAbstract) {
        throw new Exception("Unable to add " + entityType.Name +
            " to this MetadataStore.  An EntityType must have at least one property designated as a key property - See the 'DataProperty.isPartOfKey' property.");
      }

      AddStructuralType(entityType);

      return entityType;

    }

    public ComplexType AddComplexType(ComplexType complexType) {
      AddStructuralType(complexType);
      return complexType;
    }

    // TODO: think about name
    public void AddResourceName(String resourceName, Type clrType, bool isDefault = false) {
      var entityType = GetEntityType(clrType);
      AddResourceName(resourceName, entityType);
    }

    internal void AddResourceName(String resourceName, EntityType entityType, bool isDefault = false) {
      lock (_defaultResourceNameMap) {
        _resourceNameEntityTypeMap[resourceName] = entityType;
        if (isDefault) {
          _defaultResourceNameMap[entityType] = resourceName;
        }
      }
    }

    public String GetDefaultResourceName(Type clrType) {
      var entityType = GetEntityType(clrType);
      return GetDefaultResourceName(entityType);
    }

    public  string GetDefaultResourceName(EntityType entityType) {
      lock (_defaultResourceNameMap) {
        String resourceName = null;
        // give the type it's base's resource name if it doesn't have its own.
        if (!_defaultResourceNameMap.TryGetValue(entityType, out resourceName)) {
          var baseEntityType = entityType.BaseEntityType;
          if (baseEntityType != null) {
            return GetDefaultResourceName(baseEntityType);
          }
        }
        return resourceName;
      }
    }

    public static bool IsStructuralType(Type clrType) {
      return typeof(IStructuralObject).IsAssignableFrom(clrType);
    }

    #endregion

    #region Import/Export metadata

    public String ExportMetadata() {
      return ((IJsonSerializable)this).ToJNode(null).Serialize();
    }

    public TextWriter ExportMetadata(TextWriter textWriter) {
      return ((IJsonSerializable)this).ToJNode(null).SerializeTo(textWriter);
    }

    public void ImportMetadata(String metadata) {
      var jNode = JNode.DeserializeFrom(metadata);
      ImportMetadata(jNode);
    }

    public void ImportMetadata(TextReader textReader) {
      var jNode = JNode.DeserializeFrom(textReader);
      ImportMetadata(jNode);
    }

    internal void ImportMetadata(JNode jNode ) {

      DeserializeFrom(jNode);
      EntityTypes.ForEach(et => ResolveComplexTypeRefs(et));
    }

    private void ResolveComplexTypeRefs(EntityType et) {
      et.ComplexProperties.Where(cp => cp.ComplexType == null)
        .ForEach(cp => cp.ComplexType = GetComplexType(cp.ComplexTypeName));
    }

    JNode IJsonSerializable.ToJNode(Object config) {
      var jo =  new JNode(); 
      jo.AddPrimitive("metadataVersion", MetadataVersion);
      // jo.Add("name", this.Name);
      jo.AddPrimitive("namingConvention", this.NamingConvention.Name);
      // jo.AddProperty("localQueryComparisonOptions", this.LocalQueryComparisonOptions);
      jo.AddArray("dataServices", this._dataServiceMap.Values);
      jo.AddArray("structuralTypes", this._structuralTypes);
      jo.AddMap("resourceEntityTypeMap", this._resourceNameEntityTypeMap.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Name));
      return jo;
    }

    private void DeserializeFrom(JNode jNode) {
      MetadataVersion = jNode.Get<String>("metadataVersion");
      // Name
      NamingConvention = NamingConvention.FromName(jNode.Get<String>("namingConvention"));
      // localQueryComparisonOptions
      jNode.GetJNodeArray("dataServices").Select(jn => new DataService(jn)).ForEach(ds => {
        if (!_dataServiceMap.ContainsKey(ds.ServiceName)) {
          _dataServiceMap.Add(ds.ServiceName, ds);
        }
      });
      var stypes = jNode.GetJNodeArray("structuralTypes")
        .Select(jn => jn.Get<bool>("isComplexType", false) 
          ? (StructuralType)new ComplexType(jn) 
          : (StructuralType)new EntityType(jn));
      stypes.ForEach(st => this.AddStructuralType(st));

      jNode.GetMap<String>("resourceEntityTypeMap").ForEach(kvp => {
        var et = GetEntityType(kvp.Value);
        AddResourceName(kvp.Key, et);
      });
    }

    #endregion

    #region Validator methods

    internal Validator FindOrCreateValidator(JNode jNode) {
      lock (_validatorMap) {
        Validator vr;

        if (_validatorJNodeCache.TryGetValue(jNode, out vr)) {
          return vr;
        }

        vr = ValidatorFromJNode(jNode);
        _validatorJNodeCache[jNode] = vr;
        return vr;
      }
    }

    internal T InternValidator<T>(T validator) where T : Validator {
      if (validator.IsInterned) return validator;
      var jNode = validator.ToJNode();

      lock (_validatorMap) {
        if (!_validatorMap.ContainsKey(validator.Name)) {
          _validatorMap[validator.Name] = validator.GetType();
        }
        Validator cachedValidator;
        if (_validatorJNodeCache.TryGetValue(jNode, out cachedValidator)) {
          cachedValidator.IsInterned = true;
          return (T)cachedValidator;
        } else {
          _validatorJNodeCache[jNode] = validator;
          validator.IsInterned = true;
          return validator;
        }
      }
    }

    private void RegisterValidator(Type validatorType) {
      var ti = validatorType.GetTypeInfo();
      if (ti.IsAbstract) return;
      if (ti.GenericTypeParameters.Length != 0) return;
      var key = Validator.TypeToValidatorName(validatorType);
      lock (_validatorMap) {
        _validatorMap[key] = validatorType;
      }
    }

    private Validator ValidatorFromJNode(JNode jNode) {
      var vrName = jNode.Get<String>("name");
      Type vrType;
      if (!_validatorMap.TryGetValue(vrName, out vrType)) {
        var e = new Exception("Unable to create a validator for " + vrName);
        _errors.Add(e);
        return null;
      }
      // Deserialize the object
      var vr = (Validator)jNode.ToObject(vrType, true);
      return vr;
    }

    #endregion

    #region Internal and Private

    internal Type GetClrTypeFor(StructuralType stType) {
      lock (_structuralTypes) {
        return _clrTypeMap.GetClrType(stType);
      }
    }

    // T is either <ComplexType> or <EntityType>
    private T GetStructuralType<T>(Type clrType, bool okIfNotFound = false) where T : class {
      var stype = GetStructuralType(clrType, okIfNotFound);
      var ttype = stype as T;
      if (ttype != null) {
        return ttype;
      } else {
        if (okIfNotFound) return null;
        throw new Exception("Unable to find a matching " + typeof(T).Name + " for " + clrType.Name);
      }
    }

    // T is either <ComplexType> or <EntityType>
    private T GetStructuralType<T>(String typeName, bool okIfNotFound = false) where T : class {
      lock (_structuralTypes) {
        var t = _structuralTypes[typeName];
        if (t == null) {
          // locate by short name if not found by full name;
          t = _structuralTypes.FirstOrDefault(st => st.ShortName == typeName);
        }
        if (t != null) {
          var result = t as T;
          if (result == null) {
            throw new Exception("A type by this name exists but is not a " + typeof(T).Name);
          }
          return result;
        }  else if (okIfNotFound) {
          return (T)null;
        } else {
          throw new Exception("Unable to locate Type: " + typeName);
        }
      }
    }

    private void AddStructuralType(StructuralType stType, bool allowMerge = true) {
      lock (_structuralTypes) {
        // for now ignore dups
        // TODO: handle custom metadata later.
        if (_structuralTypes[stType.Name] != null) return;
        _clrTypeMap.GetClrType(stType);
        //// don't register anon types
        if (!stType.IsAnonymous) {
          if (_structuralTypes.ContainsKey(stType.Name)) {
            throw new Exception("Type " + stType.Name + " already exists in this MetadataStore.");
          }

          _structuralTypes.Add(stType);
          _shortNameMap[stType.ShortName] = stType.Name;
        }

        stType.Properties.ForEach(prop => stType.UpdateClientServerFkNames(prop));

        UpdateComplexProperties(stType);

        var entityType = stType as EntityType;
        if (entityType != null) {

          UpdateNavigationProperties(entityType);

          // check if this structural type's name, short version or qualified version has a registered ctor.
          //  structuralType.getEntityCtor();
          if (entityType.BaseEntityType != null) {
            entityType.BaseEntityType.AddSubEntityType(entityType);
          }
        }
      }
    }

    //private bool MatchType(Type clrType, StructuralType stType) {
    //  return (clrType.Name == stType.ShortName && clrType.Namespace == stType.Namespace);
    //}

    private void UpdateNavigationProperties(EntityType entityType) {
      entityType.NavigationProperties.ForEach(np => {
        if (np.EntityType != null) return;
        if (!ResolveNp(np)) {
          AddIncompleteNavigationProperty(np.EntityTypeName, np);
        }
      });

      GetIncompleteNavigationProperties(entityType).ForEach(np => ResolveNp(np));
      _incompleteTypeMap.Remove(entityType.Name);
    }

    private bool ResolveNp(NavigationProperty np) {
      var entityType = GetEntityType(np.EntityTypeName, true);
      if (entityType == null) return false;
      np.EntityType = entityType;
      var invNp = entityType.NavigationProperties.FirstOrDefault(altNp => {
        // Can't do this because of possibility of comparing a base class np with a subclass altNp.
        //return altNp.associationName === np.associationName
        //    && altNp !== np;
        // So use this instead.
        return altNp.AssociationName == np.AssociationName &&
            (altNp.Name != np.Name || altNp.EntityTypeName != np.EntityTypeName);
      });
      np.Inverse = invNp;
      if (invNp == null) {
        // unidirectional 1-n relationship
        np.InvForeignKeyProperties.ForEach(invFkProp => {
          
          invFkProp.IsForeignKey = true;
          var invEntityType = (EntityType)np.ParentType;
          
          invFkProp.InverseNavigationProperty = invEntityType.NavigationProperties.FirstOrDefault(np2 => {
            return np2.InvForeignKeyNames.IndexOf(invFkProp.Name) >= 0 && np2.EntityType == invFkProp.ParentType;
          });


        });
      }

      ResolveRelated(np);
      return true;
    }

    // sets navigation property: relatedDataProperties and dataProperty: relatedNavigationProperty
    private void ResolveRelated(NavigationProperty np) {

      var fkProps = np.ForeignKeyProperties;

      fkProps.ForEach(dp => {
        dp.RelatedNavigationProperty = np;
        np.EntityType._inverseForeignKeyProperties.Add(dp);
        dp.IsForeignKey = true;
        np._relatedDataProperties.Add(dp);
      });
    }

    private void UpdateComplexProperties(StructuralType structuralType) {

      structuralType.ComplexProperties.ForEach(cp => {
        if (cp.ComplexType != null) return;
          cp.DataType = null;
          cp.DefaultValue = null;
          var complexType = GetComplexType(cp.ComplexTypeName, true);
          if (complexType == null) {
            AddIncompleteComplexProperty(cp.ComplexTypeName, cp);
          } else {
            cp.ComplexType = complexType;
        }
      });

      if (!structuralType.IsEntityType) {
        var incompleteProps = GetIncompleteComplexProperties(structuralType.Name);
        incompleteProps.ForEach(cp => cp.ComplexType = GetComplexType(cp.ComplexTypeName));
      }
    }

    private IEnumerable<NavigationProperty> GetIncompleteNavigationProperties(EntityType entityType) {
      List<NavigationProperty> results;
      if (_incompleteTypeMap.TryGetValue(entityType.Name, out results)) {
        return results;
      } else {
        return Enumerable.Empty<NavigationProperty>();
      }
    }

    private IEnumerable<DataProperty> GetIncompleteComplexProperties(String structuralTypeName) {
      // destructive get routine - deliberately
      List<DataProperty> results;
      if (_incompleteComplexTypeMap.TryGetValue(structuralTypeName, out results)) {
        _incompleteComplexTypeMap.Remove(structuralTypeName);
        return results;
      } else {
        return Enumerable.Empty<DataProperty>();
      }
    }

    private void AddIncompleteNavigationProperty(String entityTypeName, NavigationProperty np) {
      List<NavigationProperty> results;
      if (_incompleteTypeMap.TryGetValue(entityTypeName, out results)) {
        results.Add(np);
      } else {
        _incompleteTypeMap.Add(entityTypeName, new List<NavigationProperty> { np });
      }
    }

    private void AddIncompleteComplexProperty(String structuralTypeName, DataProperty dp) {
      List<DataProperty> results;
      if (_incompleteComplexTypeMap.TryGetValue(structuralTypeName, out results)) {
        results.Add(dp);
      } else {
        _incompleteComplexTypeMap.Add(structuralTypeName, new List<DataProperty> { dp });
      }
    }

    #endregion

    #region Inner classes 

    // inner class
    internal class ClrTypeMap {
      public ClrTypeMap(MetadataStore metadataStore) {
        _metadataStore = metadataStore;
      }

      public StructuralType GetStructuralType(Type clrType) {
        var stName = StructuralType.ClrTypeToStructuralTypeName(clrType);
        TypePair tp;
        if (_map.TryGetValue(stName, out tp)) {
          var stType = tp.StructuralType;
          if (tp.ClrType == null) {
            tp.ClrType = clrType;
            _metadataStore.ProbeAssemblies(new Assembly[] { clrType.GetTypeInfo().Assembly });
          }
          return stType;
        } else {
          _map.Add(stName, new TypePair() { ClrType = clrType });
          return null;
        }
      }

      public Type GetClrType(StructuralType stType) {
        TypePair tp;
        if (_map.TryGetValue(stType.Name, out tp)) {
          stType.ClrType = tp.ClrType;
          if (tp.StructuralType == null) {
            tp.StructuralType = stType;
          }
          return tp.ClrType;
        } else {
          _map.Add(stType.Name, new TypePair() { StructuralType = stType });
          return null;
        }
      }

      private MetadataStore _metadataStore;
      private Dictionary<String, TypePair> _map = new Dictionary<String, TypePair>();

      private class TypePair {
        public Type ClrType;
        public StructuralType StructuralType;
      }
    }

    #endregion

    #region Internal vars;

    internal static String ANONTYPE_PREFIX = "_IB_";

    #endregion

    #region Private vars

    private static MetadataStore __instance = new MetadataStore();
    private static readonly Object __lock = new Object();
    
    private readonly AsyncSemaphore _asyncSemaphore = new AsyncSemaphore(1);
    private Object _lock = new Object();

    // lock using _dataServiceMap
    private Dictionary<String, DataService> _dataServiceMap = new Dictionary<String, DataService>();
    private NamingConvention _namingConvention = NamingConvention.Default;
    // locked using _structuralTypes
    private ClrTypeMap _clrTypeMap;
    private HashSet<Assembly> _probedAssemblies = new HashSet<Assembly>();
    private List<Tuple<Type, Action<Type>, Func<Assembly, bool>>> _typeDiscoveryActions = new List<Tuple<Type, Action<Type>, Func<Assembly, bool>>>();
    private Dictionary<Type, Action<Object>> _typeInitializerMap = new Dictionary<Type, Action<object>>();
    private StructuralTypeCollection _structuralTypes = new StructuralTypeCollection();
    private Dictionary<String, String> _shortNameMap = new Dictionary<string, string>();
    private Dictionary<String, List<NavigationProperty>> _incompleteTypeMap = new Dictionary<String, List<NavigationProperty>>(); // key is typeName
    private Dictionary<String, List<DataProperty>> _incompleteComplexTypeMap = new Dictionary<String, List<DataProperty>>();   // key is typeName
    

    // locked using _resourceNameEntityTypeMap
    private Dictionary<EntityType, String> _defaultResourceNameMap = new Dictionary<EntityType, string>();
    private Dictionary<String, EntityType> _resourceNameEntityTypeMap = new Dictionary<string, EntityType>();

    // validator related. - both locked using _validatorMap
    private Dictionary<String, Type> _validatorMap = new Dictionary<string, Type>();
    private Dictionary<JNode, Validator> _validatorJNodeCache = new Dictionary<JNode, Validator>();
    

    private List<Exception> _errors = new List<Exception>();

    #endregion

  }







}
