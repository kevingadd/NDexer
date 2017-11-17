﻿#if SystemData

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data;
using System.Threading;
using System.Linq.Expressions;
using Squared.Task;
using Squared.Threading;

namespace Squared.Task.Data {
    [Mapper.Mapper]
    internal class SequenceItem<T> {
        [Mapper.Column(0)]
        public T Value {
            get;
            set;
        }
    }

    public struct NamedParam {
        public string Name;
        public object Value;

        public string N {
            set {
                Name = value;
            }
        }

        public object V {
            set {
                Value = value;
            }
        }
    }

    public class QueryDataReader : IDisposable {
        public readonly Query Query;
        public readonly IDataReader Reader;
        public readonly IFuture Future;
        private readonly Action<IFuture> CompletionNotifier;

        public QueryDataReader (Query query, IDataReader reader, IFuture future) {
            Query = query;
            Reader = reader;
            CompletionNotifier = Query.GetCompletionNotifier();
            Future = future;
        }

        public void Dispose () {
            try {
                Reader.Close();
            } catch {
            }
            try {
                Reader.Dispose();
            } catch {
            }
            CompletionNotifier(Future);
        }
    }

    public class Query : IDisposable {
        protected int _NumberOfOutstandingQueries = 0;

        public struct ParameterCollection : IEnumerable<IDataParameter> {
            public readonly Query Query;

            public ParameterCollection (Query query) {
                Query = query;
            }

            public IDataParameter this[int index] {
                get {
                    return (IDataParameter)Query.Command.Parameters[index];
                }
            }

            public IDataParameter this[string name] {
                get {
                    return (IDataParameter)Query.Command.Parameters[name];
                }
            }

            public int Count {
                get {
                    return Query.Command.Parameters.Count;
                }
            }

            public IEnumerator<IDataParameter> GetEnumerator () {
                foreach (var p in Query.Command.Parameters)
                    yield return (IDataParameter)p;
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator () {
                return Query.Command.Parameters.GetEnumerator();
            }
        }

        ConnectionWrapper _Manager;
        IDbCommand _Command;

        internal Query (ConnectionWrapper manager, IDbCommand command) {
            _Manager = manager;
            _Command = command;
        }

        internal void ValidateParameters (object[] parameters) {
            if (_Manager == null)
                throw new ObjectDisposedException("query");

            if (parameters.Length != _Command.Parameters.Count) {
                string errorString = String.Format("Got {0} parameter(s), expected {1}.", parameters.Length, _Command.Parameters.Count);
                throw new InvalidOperationException(errorString);
            }

            for (int i = 0; i < parameters.Length; i++) {
                var value = parameters[i];
                if (value is NamedParam) {
                    var namedParam = (NamedParam)value;
                    var parameter = (IDbDataParameter)_Command.Parameters[namedParam.Name];
                    if (parameter == null)
                        throw new InvalidOperationException();
                }
            }
        }

        internal void BindParameters (object[] parameters) {
            if (_Manager == null)
                throw new ObjectDisposedException("query");

            for (int i = 0; i < parameters.Length; i++) {
                var value = parameters[i];
                if (value is NamedParam) {
                    var namedParam = (NamedParam)value;
                    var parameter = (IDbDataParameter)_Command.Parameters[namedParam.Name];
                    parameter.Value = namedParam.Value;
                } else {
                    var parameter = (IDbDataParameter)_Command.Parameters[i];
                    parameter.Value = value;
                }
            }
        }

        private Action GetExecuteFunc<T> (object[] parameters, Func<IFuture, T> queryFunc, Future<T> future) {
            if (_Manager == null)
                throw new ObjectDisposedException("query");

            return () => {
                try {
                    BindParameters(parameters);
                    T result = queryFunc(future);
                    future.SetResult(result, null);
                } catch (Exception e) {
                    future.Fail(e);
                }
            };
        }

        private Future<T> InternalExecuteQuery<T> (object[] parameters, Func<IFuture, T> queryFunc, bool suspendCompletion) {
            if (_Manager == null || _Manager.Closed)
                throw new ObjectDisposedException("query");

            ValidateParameters(parameters);
            var f = new Future<T>();
            var m = _Manager;

            OnDispose od = (_) => {
                Interlocked.Decrement(ref _NumberOfOutstandingQueries);
                m.NotifyQueryCompleted(f);
            };
            f.RegisterOnDispose(od);

            if (suspendCompletion) {
                OnComplete oc = (_) => {
                    Interlocked.Decrement(ref _NumberOfOutstandingQueries);
                    if (_.Failed)
                        m.NotifyQueryCompleted(f);
                };

                f.RegisterOnComplete(oc);
            } else {
                OnComplete oc = (_) => {
                    Interlocked.Decrement(ref _NumberOfOutstandingQueries);
                    m.NotifyQueryCompleted(f);
                };

                f.RegisterOnComplete(oc);
            }

            Interlocked.Increment(ref _NumberOfOutstandingQueries);
            Action ef = GetExecuteFunc(parameters, queryFunc, f);
            m.EnqueueQuery(f, ef);

            return f;
        }

        internal Action<IFuture> GetCompletionNotifier () {
            Action<IFuture> cn = (f) => _Manager.NotifyQueryCompleted(f);
            return cn;
        }

        public Future<int> ExecuteNonQuery (params object[] parameters) {
            Func<IFuture, int> queryFunc = (f) => {
                _Manager.SetActiveQueryObject(this);
                if (_Command != null)
                    return _Command.ExecuteNonQuery();
                else
                    throw new ObjectDisposedException("query");
            };
            return InternalExecuteQuery(parameters, queryFunc, false);
        }

        public Future<object> ExecuteScalar (params object[] parameters) {
            Func<IFuture, object> queryFunc = (f) => {
                _Manager.SetActiveQueryObject(this);
                return _Command.ExecuteScalar();
            };
            return InternalExecuteQuery(parameters, queryFunc, false);
        }

        public Future<T> ExecuteScalar<T> (params object[] parameters) {
            Func<IFuture, T> queryFunc = (f) => {
                _Manager.SetActiveQueryObject(this);
                return (T)_Command.ExecuteScalar();
            };
            return InternalExecuteQuery(parameters, queryFunc, false);
        }

        public Future<T> ExecuteScalar<T> (Expression<Func<T>> target, params object[] parameters) {
            var f = ExecuteScalar<T>(parameters);
            f.Bind<T>(target);
            return f;
        }

        public Future<QueryDataReader> ExecuteReader (params object[] parameters) {
            Func<IFuture, QueryDataReader> queryFunc = (f) => {
                _Manager.SetActiveQueryObject(this);
                return new QueryDataReader(this, _Command.ExecuteReader(), f);
            };
            return InternalExecuteQuery(parameters, queryFunc, true);
        }

        public Future<QueryDataReader> ExecuteReader (CommandBehavior behavior, params object[] parameters) {
            Func<IFuture, QueryDataReader> queryFunc = (f) => {
                _Manager.SetActiveQueryObject(this);
                return new QueryDataReader(this, _Command.ExecuteReader(behavior), f);
            };
            return InternalExecuteQuery(parameters, queryFunc, true);
        }

        public Future<string[]> GetColumnNames (params object[] parameters) {
            Func<IFuture, string[]> queryFunc = (f) => {
                var names = new List<string>();
                _Manager.SetActiveQueryObject(this);

                using (var reader = _Command.ExecuteReader())
                for (int i = 0; i < reader.FieldCount; i++)
                    names.Add(reader.GetName(i));

                return names.ToArray();
            };

            return InternalExecuteQuery(parameters, queryFunc, false);
        }

        public TaskEnumerator<IDataRecord> Execute (params object[] parameters) {
            var fReader = this.ExecuteReader(parameters);

            var e = new TaskEnumerator<IDataRecord>(ExecuteTask(fReader), 1);
            e.OnEarlyDispose = () => {
                fReader.Dispose();

                if (fReader.Completed)
                    fReader.Result.Dispose();
            };

            return e;
        }

        protected static IEnumerator<object> ExecuteTask (Future<QueryDataReader> fReader) {
            yield return fReader;

            using (var reader = fReader.Result) {
                Func<bool> moveNext = () =>
                    reader.Reader.Read();
                var nv = new NextValue(reader.Reader);

                while (true) {
                    var f = Future.RunInThread(moveNext);
                    yield return f;

                    if (f.Result == false)
                        break;
                    else
                        yield return nv;
                }
            }
        }

        public Future<T[]> ExecuteArray<T> (params object[] parameters)
            where T : class, new() {

            var ca = typeof(T).GetCustomAttributes(
                typeof(Squared.Task.Data.Mapper.MapperAttribute), false
            );
            if ((ca != null) && (ca.Length > 0)) {
                var fResult = new Future<T[]>();

                _Manager.Scheduler.Start(
                    fResult,
                    new SchedulableGeneratorThunk(ExecuteMapperArrayTask<T>(parameters)),
                    TaskExecutionPolicy.RunWhileFutureLives
                );

                return fResult;
            } else {
                return ExecutePrimitiveArray<T>(parameters);
            }
        }

        public Future<T[]> ExecutePrimitiveArray<T> (params object[] parameters) {
            var fResult = new Future<T[]>();

            _Manager.Scheduler.Start(
                fResult,
                new SchedulableGeneratorThunk(ExecuteArrayTask<T>(parameters)),
                TaskExecutionPolicy.RunWhileFutureLives
            );

            return fResult;
        }

        protected IEnumerator<object> ExecuteMapperArrayTask<T> (params object[] parameters)
            where T : class, new() {

            var result = new List<T>();

            using (var e = this.Execute<T>(parameters))
                while (!e.Disposed) {
                    yield return e.Fetch();

                    foreach (var item in e)
                        result.Add(item);
                }

            yield return new Result(result.ToArray());
        }

        protected IEnumerator<object> ExecuteArrayTask<T> (params object[] parameters) {
            var result = new List<T>();

            using (var e = this.Execute<SequenceItem<T>>(parameters))
            while (!e.Disposed) {
                yield return e.Fetch();

                foreach (var item in e)
                    result.Add(item.Value);
            }

            yield return new Result(result.ToArray());
        }

        public TaskEnumerator<T> Execute<T> (params object[] parameters)
            where T : class, new() {
            var fReader = this.ExecuteReader(parameters);

            var e = new TaskEnumerator<T>(ExecuteMapper<T>(fReader));
            e.OnEarlyDispose = () => {
                fReader.Dispose();

                if (fReader.Completed)
                    fReader.Result.Dispose();
            };

            return e;
        }

        protected static IEnumerator<object> ExecuteMapper<T> (Future<QueryDataReader> fReader)
            where T : class, new() {
            yield return fReader;

            using (var reader = fReader.Result) {
                var mapper = new Mapper.Mapper<T>(reader.Reader);

                using (var e = EnumeratorExtensionMethods.EnumerateViaThreadpool(
                    mapper.ReadSequence(), TaskEnumerator<T>.DefaultBufferSize
                ))
                while (e.MoveNext()) {
                    var v = e.Current;

                    yield return v;
                }
            }
        }

        public TaskEnumerator<T> Execute<T> (Func<IDataReader, T> customMapper, params object[] parameters) {
            var fReader = this.ExecuteReader(parameters);

            var e = new TaskEnumerator<T>(ExecuteCustomMapper<T>(customMapper, fReader));
            e.OnEarlyDispose = () => {
                fReader.Dispose();

                if (fReader.Completed)
                    fReader.Result.Dispose();
            };

            return e;
        }

        protected static IEnumerator<T> CustomMapperWrapper<T> (IDataReader reader, Func<IDataReader, T> customMapper) {
            using (reader)
            while (reader.Read())
                yield return customMapper(reader);
        }

        protected static IEnumerator<object> ExecuteCustomMapper<T> (Func<IDataReader, T> customMapper, Future<QueryDataReader> fReader) {
            yield return fReader;

            using (var reader = fReader.Result) {
                using (var e = EnumeratorExtensionMethods.EnumerateViaThreadpool(
                    CustomMapperWrapper<T>(reader.Reader, customMapper), 
                    TaskEnumerator<T>.DefaultBufferSize
                ))
                    while (e.MoveNext()) {
                        var v = e.Current;

                        yield return v;
                    }
            }
        }

        public ParameterCollection Parameters {
            get {
                return new ParameterCollection(this);
            }
        }

        public IDbCommand Command {
            get {
                return _Command;
            }
        }

        public void Dispose () {
            if (_NumberOfOutstandingQueries > 0)
                throw new InvalidOperationException("You cannot dispose a query while it is currently executing.");

            if (_Command != null) {
                _Command.Dispose();
                _Command = null;
            }
        }
    }
}

#endif