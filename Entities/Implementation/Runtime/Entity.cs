﻿using Neon.Collections;
using Neon.Entities.Serialization;
using Neon.Serialization;
using Neon.Utilities;
using System;
using System.Collections.Generic;

namespace Neon.Entities {
    internal class Entity : IEntity {
        public SerializedEntity ToSerializedEntity(bool entityIsAdding, bool entityIsRemoving,
            SerializationConverter converter) {
            List<DataAccessor> modified = _concurrentModifications.ToList();

            List<SerializedEntityData> serializedDataList = new List<SerializedEntityData>();
            foreach (var tuple in _data) {
                int id = tuple.Key;
                DataAccessor accessor = new DataAccessor(id);
                DataContainer container = tuple.Value;

                SerializedEntityData serializedData = new SerializedEntityData() {
                    DataType = container.Current.GetType().ToString(),
                    IsAdding = false,
                    IsRemoving = IsRemoving(accessor)
                };

                Type dataType = container.Current.GetType();

                if (modified.Contains(accessor)) {
                    serializedData.WasModified = true;
                    serializedData.PreviousState = converter.Export(dataType, container.Current);
                    serializedData.CurrentState = converter.Export(dataType, container.Modifying);
                }

                else {
                    serializedData.WasModified = false;
                    serializedData.PreviousState = converter.Export(dataType, container.Previous);
                    serializedData.CurrentState = converter.Export(dataType, container.Current);
                }

                serializedDataList.Add(serializedData);
            }

            foreach (var addedData in _toAdd) {
                Type dataType = addedData.GetType();
                DataAccessor accessor = new DataAccessor(dataType);

                SerializedEntityData serializedData = new SerializedEntityData() {
                    DataType = addedData.GetType().ToString(),
                    WasModified = false, // doesn't matter
                    IsAdding = true, // always true
                    IsRemoving = false, // doesn't matter
                    PreviousState = converter.Export(dataType, addedData), // doesn't matter
                    CurrentState = converter.Export(dataType, addedData)
                };
                serializedDataList.Add(serializedData);
            }

            return new SerializedEntity() {
                PrettyName = PrettyName,
                UniqueId = _uniqueId,
                Data = serializedDataList,
                IsAdding = entityIsAdding,
                IsRemoving = entityIsRemoving
            };
        }

        #region Pretty Name
        /// <summary>
        /// The Entity's pretty name, used for debugging / printing purposes.
        /// </summary>
        /// <remarks>
        /// If the entity does not have a pretty name, then this value is set to an empty string.
        /// </remarks>
        public string PrettyName {
            get;
            set;
        }
        #endregion

        #region Unique ID
        /// <summary>
        /// Generates unique identifiers.
        /// </summary>
        private static UniqueIntGenerator _idGenerator = new UniqueIntGenerator();

        // thread safe because we never write
        private int _uniqueId;

        int IEntity.UniqueId {
            get { return _uniqueId; }
        }
        #endregion

        #region Metadata
        public EntityManagerMetadata Metadata = new EntityManagerMetadata();
        #endregion

        #region Event Processor
        private EventNotifier _eventProcessor;
        IEventNotifier IQueryableEntity.EventNotifier {
            get { return _eventProcessor; }
        }
        #endregion

        /// <summary>
        /// Reconstructs an entity with the given unique id and the set of restored data instances.
        /// </summary>
        /// <remarks>
        /// Notice, however, that this function does *NOT* notify the EntityManager if a data
        /// instance has been restored which has a modification or a state change.
        /// </remarks>
        /// <param name="addingToEntityManager">Is this entity going to end up in an EntityManager
        /// instance? This has implications on how internal state is managed.</param>
        public void Restore(SerializedEntity serializedEntity, SerializationConverter converter,
            out bool hasModification, out bool hasStateChange, bool addingToEntityManager) {

            PrettyName = serializedEntity.PrettyName ?? "";
            _uniqueId = serializedEntity.UniqueId;
            _idGenerator.Consume(_uniqueId);
            _eventProcessor = new EventNotifier();

            DataStateChangeNotifier = new Notifier<Entity>(this);
            ModificationNotifier = new Notifier<Entity>(this);

            hasModification = false;
            hasStateChange = false;

            foreach (var data in serializedEntity.Data) {
                hasStateChange = hasStateChange || data.IsAdding || data.IsRemoving;
                hasModification = hasModification || data.WasModified;

                if (data.IsAdding) {
                    IData current = data.GetDeserializedCurrentState(converter);
                    _toAdd.Add(current);
                }

                else {
                    IData current = data.GetDeserializedCurrentState(converter);
                    IData previous = data.GetDeserializedPreviousState(converter);

                    int id = DataAccessorFactory.GetId(current.GetType());
                    _data[id] = new DataContainer(previous, current, current.Duplicate());

                    // There is going to be an ApplyModification call before systems actually view
                    // this Entity instance. With that in mind, we can treat our data initialization
                    // as if if were operating on the previous frame.

                    if (data.WasModified) {
                        // This is kind of an ugly hack, because to get the correct Previous/Current
                        // data we need to move Previous into Current so that Previous will reflect
                        // the true Previous value, not the Current one.

                        // Internal signal that a modification is going to take place
                        ((IEntity)this).Modify(new DataAccessor(id));

                        // Move Previous into Current, so that after the ApplyModification we have
                        // the correct data values
                        _data[id].Current.CopyFrom(_data[id].Previous);
                    }

                    if (data.IsRemoving) {
                        ((IEntity)this).RemoveData(new DataAccessor(id));
                    }
                }
            }

            // if we're not going to be added to an entity manager, then we should apply
            // modifications so that previous and current map to the correct values
            if (addingToEntityManager == false) {
                ApplyModifications();
                DataStateChangeUpdate();
            }
        }

        public Entity()
            : this(_idGenerator.Next(), "") {
        }

        public Entity(int uniqueId, string prettyName) {
            PrettyName = prettyName;
            _uniqueId = uniqueId;
            _idGenerator.Consume(uniqueId);

            _eventProcessor = new EventNotifier();

            DataStateChangeNotifier = new Notifier<Entity>(this);
            ModificationNotifier = new Notifier<Entity>(this);
        }

        #region Private EntityManager only API
        public Notifier<Entity> DataStateChangeNotifier;
        public Notifier<Entity> ModificationNotifier;

        public GameEngine GameEngine;

        /// <summary>
        /// Removes all data instances from the Entity.
        /// </summary>
        internal void RemoveAllData() {
            // TODO: potentially optimize this method
            foreach (var tuple in _data) {
                DataAccessor accessor = new DataAccessor(tuple.Value.Current.GetType());
                ((IEntity)this).RemoveData(accessor);
            }
        }

        private void DoModifications() {
            _modifiedLastFrame.Clear();

            // apply modifications
            _concurrentModifications.IterateAndClear(dataAccessor => {
                int id = dataAccessor.Id;

                // if we removed the data, then don't bother apply/dispatching modifications on it
                if (_data.Contains(id)) {
                    _data[id].MotificationActivation.Reset();
                    _data[id].Increment();

                    _modifiedLastFrame[id] = _data[id];
                }
            });
        }

        public void ApplyModifications() {
            DoModifications();

            ModificationNotifier.Reset();

            // We do *not* reset the DataStateChangeNotifier here. This may seem unusual, but the
            // reason is for efficiency. This implementation is tightly coupled with the
            // EntityManager. The EntityManager contains a list of Entities with data state changes
            // (that systems use to check to see if an entity needs to be contained within it).
            // Because data state changes run for multiple updates, that list can contain an Entity
            // even if a modification has been applied. Activating the notifier inserts the Entity
            // into the list. Reseting it effectively says that the Entity is no longer in the data
            // state change list. Therefore, the EntityManager knows best when the entity is not in
            // the list and is therefore responsible for reseting the notifier.

            // DataStateChangeNotifier.Reset(); do not uncomment me; see above
        }

        /// <summary>
        /// Applies data state changes to the entity.
        /// </summary>
        /// <remarks>
        /// This function is not thread-safe; no other API calls can be made to the Entity while
        /// this function is being executed.
        /// </remarks>
        /// <returns>If more data state change updates are needed</returns>
        public void DataStateChangeUpdate() {
            // do removals
            {
                List<DataAccessor> removedStage2 = _toRemove.Previous;
                List<DataAccessor> removedStage1 = _toRemove.Current;
                _toRemove.Swap();

                for (int i = 0; i < removedStage1.Count; ++i) {
                    int id = removedStage1[i].Id;
                    _removed[id] = removedStage1[i];
                    // _removed[id] is removed from _removed in stage2

                    _eventProcessor.Submit(new RemovedDataEvent(_data[id].Current.GetType()));
                }

                for (int i = 0; i < removedStage2.Count; ++i) {
                    int id = removedStage2[i].Id;
                    _removed.Remove(id);
                    _data.Remove(id);
                }
                removedStage2.Clear();
            }

            // We don't throw an exception immediately. If we are throwing an exception, that means
            // that the Entity is in an invalid state (adding a bad data instance). However, the
            // only way to recover from that invalid state is by having this method terminate.
            // Hence, we only throw an exception at the end of the method.
            Exception exceptionToThrow = null;

            // do additions
            for (int i = 0; i < _toAdd.Count; ++i) {
                IData added = _toAdd[i];
                int id = DataAccessorFactory.GetId(added.GetType());

                // make sure we do not readd the same data instance twice
                if (_data.Contains(id)) {
                    exceptionToThrow = new AlreadyAddedDataException(this, new DataAccessor(added));
                    continue;
                }

                _data[id] = new DataContainer(added.Duplicate(), added.Duplicate(), added.Duplicate());

                // visualize the initial data
                _eventProcessor.Submit(new AddedDataEvent(added.GetType()));
            }
            _toAdd.Clear();

            if (exceptionToThrow != null) {
                throw exceptionToThrow;
            }
        }

        internal bool NeedsMoreDataStateChangeUpdates() {
            // do we still have things to remove or to add?
            return _toRemove.Previous.Count > 0 || _toRemove.Current.Count > 0 || _toAdd.Count > 0;
        }
        #endregion

        void IEntity.Destroy() {
            // EntityManager.RemoveEntity is synchronized
            GameEngine.RemoveEntity(this);
        }

        #region Instance data
        /// <summary>
        /// The data contained within the Entity. One item in the tuple is the current state and one
        /// item is the next state.
        /// </summary>
        /// <remarks>
        /// Only the entity manager calls entity APIs that write to this; it is single-threaded
        /// only.
        /// </remarks>
        private SparseArray<DataContainer> _data = new SparseArray<DataContainer>();

        /// <summary>
        /// Data that has been modified this frame and needs to be pushed out
        /// </summary>
        private SparseArray<DataContainer> _modifiedLastFrame = new SparseArray<DataContainer>();
        private ConcurrentWriterBag<DataAccessor> _concurrentModifications = new ConcurrentWriterBag<DataAccessor>();

        /// <summary>
        /// Items that are pending addition in the next update call
        /// </summary>
        private List<IData> _toAdd = new List<IData>();

        private SwappableItem<List<DataAccessor>> _toRemove = new SwappableItem<List<DataAccessor>>(new List<DataAccessor>(), new List<DataAccessor>());
        private SparseArray<DataAccessor> _removed = new SparseArray<DataAccessor>();
        #endregion

        #region Helper Methods
        /// <summary>
        /// Attempts to retrieve a data instance with the given DataAccessor from the list of added
        /// data.
        /// </summary>
        /// <param name="accessor">The DataAccessor to lookup</param>
        /// <returns>A data instance, or null if it cannot be found</returns>
        private IData GetAddedData_unlocked(DataAccessor accessor) {
            int id = accessor.Id;
            // TODO: optimize this so we don't have to search through all added data... though
            // this should actually be pretty quick
            for (int i = 0; i < _toAdd.Count; ++i) {
                int addedId = DataAccessorFactory.GetId(_toAdd[i].GetType());
                if (addedId == id) {
                    return _toAdd[i];
                }
            }

            return null;
        }
        #endregion

        IData IEntity.AddOrModify(DataAccessor accessor) {
            if (((IEntity)this).ContainsData(accessor) == false) {
                lock (_toAdd) {
                    IData added = GetAddedData_unlocked(accessor);
                    if (added == null) {
                        added = AddData_unlocked(accessor);
                    }

                    return added;
                }
            }

            return ((IEntity)this).Modify(accessor);
        }

        ICollection<IData> IQueryableEntity.SelectCurrentData(Predicate<IData> filter, ICollection<IData> storage) {
            if (storage == null) {
                storage = new List<IData>();
            }

            foreach (var tuple in _data) {
                IData data = tuple.Value.Current;
                if (filter == null || filter(data)) {
                    storage.Add(data);
                }
            }

            return storage;
        }

        #region AddData
        private IData AddData_unlocked(DataAccessor accessor) {
            // ensure that we have not already added a data of this type
            if (GetAddedData_unlocked(accessor) != null) {
                throw new AlreadyAddedDataException(this, accessor);
            }

            // add our data
            Type dataType = DataAccessorFactory.GetTypeFromAccessor(accessor);
            IData data = (IData)Activator.CreateInstance(dataType);
            _toAdd.Add(data);

            // notify the entity manager
            ModificationNotifier.Notify();
            DataStateChangeNotifier.Notify();

            // return the new instance
            return data;
        }

        IData IEntity.AddData(DataAccessor accessor) {
            lock (_toAdd) {
                return AddData_unlocked(accessor);
            }
        }
        #endregion

        #region RemoveData
        void IEntity.RemoveData(DataAccessor accessor) {
            lock (_toRemove.Current) {
                _toRemove.Current.Add(accessor);
            }

            ModificationNotifier.Notify();
            DataStateChangeNotifier.Notify();
        }
        #endregion

        #region Modify
        IData IEntity.Modify(DataAccessor accessor) {
            var id = accessor.Id;

            if (((IEntity)this).ContainsData(accessor) == false) {
                IData added = GetAddedData_unlocked(accessor);
                if (added != null) {
                    return added;
                }

                throw new NoSuchDataException(this, accessor);
            }

            if (_data[id].MotificationActivation.TryActivate()) {
                _concurrentModifications.Add(accessor);
                ModificationNotifier.Notify();
            }
            else if (_data[id].Current.SupportsConcurrentModifications == false) {
                throw new RemodifiedDataException(this, accessor);
            }

            return _data[id].Modifying;
        }
        #endregion

        IData IQueryableEntity.Current(DataAccessor accessor) {
            if (_data.Contains(accessor.Id) == false) {
                throw new NoSuchDataException(this, accessor);
            }

            return _data[accessor.Id].Current;
        }

        IData IQueryableEntity.Previous(DataAccessor accessor) {
            if (((IEntity)this).ContainsData(accessor) == false) {
                throw new NoSuchDataException(this, accessor);
            }

            return _data[accessor.Id].Previous;
        }

        #region ContainsData
        bool IQueryableEntity.ContainsData(DataAccessor accessor) {
            int id = accessor.Id;
            return _data.Contains(id) && _removed.Contains(id) == false;
        }
        #endregion

        bool IQueryableEntity.WasModified(DataAccessor accessor) {
            return _modifiedLastFrame.Contains(accessor.Id);
        }

        /// <summary>
        /// Returns all data instances that are being added.
        /// </summary>
        public List<IData> GetAddingData() {
            return _toAdd;
        }

        /// <summary>
        /// Returns true if, a) there is data for the given accessor, and b) if the data is slated
        /// for removal in the next data state update.
        /// </summary>
        public bool IsRemoving(DataAccessor accessor) {
            return _toRemove.Current.Contains(accessor);
        }

        public override string ToString() {
            if (PrettyName.Length > 0) {
                return string.Format("Entity [uid={0}, name={1}]", _uniqueId, PrettyName);
            }
            else {
                return string.Format("Entity [uid={0}]", _uniqueId);
            }
        }
    }
}