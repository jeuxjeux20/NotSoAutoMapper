﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace HandmadeMapper.ExpressionProcessing
{
    /// <summary>
    /// An expression transformer that unwraps <c>Mapper.Include</c> calls with the mapper's expression.
    /// </summary>
    public sealed class UnwrapExpressionTransformer : ExpressionVisitor, IExpressionTransformer
    {
        /// <summary>
        /// The default mapper resolvers to use.
        /// </summary>
        public static readonly List<IMapperResolver> DefaultMapperResolvers = new List<IMapperResolver>(0);

        private readonly IEnumerable<IMapperResolver> _mapperResolvers;
        private MappingContext _currentContext = null!; // Should always be used with Transform<T> anyway.

        /// <summary>
        /// Creates a new <see cref="UnwrapExpressionTransformer"/>.
        /// </summary>
        /// <param name="mapperResolvers">The mapper resolvers to use. Defaults to <see cref="DefaultMapperResolvers"/></param>
        public UnwrapExpressionTransformer(IEnumerable<IMapperResolver>? mapperResolvers = null)
        {
            _mapperResolvers = mapperResolvers ?? DefaultMapperResolvers.ToArray();
        }

        /// <inheritdoc />
        public Expression<T> Transform<T>(Expression<T> source, MappingContext context)
        {
            _currentContext = context;
            return (Expression<T>)Visit(source);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1062:Validate arguments of public methods",
            Justification = "It's an ExpressionVisitor.")]
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member: It's an expression visitor
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // The goal is to replace Mapper.Include with the mapper's expression, using the given source.

            // Let's say we have:
            //  Mapper.Include(x.Thing, mapper)
            // And mapper's expression is:
            //  y = new Something { Cat = y.Cat }

            // And we will replace like this:
            // Mapper.Include(x.Thing, mapper) -> new Something { Cat = x.Thing.Cat }

            if (node.Object is null && // Is static
                node.Method.DeclaringType == typeof(Mapper) && // Mapper.???
                node.Method.Name == nameof(Mapper.Include)) // Mapper.Include(x.Thing, mapper)
            {
                var sourceArgument = node.Arguments[0]; // x.Thing
                var mapperArgument = node.Arguments.ElementAtOrDefault(1); // mapper

                Expression expression;
                if (mapperArgument != null)
                {
                    // There we create something like "() => mapper", from "Mapper.Include(x.Thing, mapper)".
                    // Then we use that fresh lambda to get the actual mapper.
                    var mapperArgumentGetter =
                        Expression.Lambda(mapperArgument).Compile() as Func<object?>;
                    var mapperResult = mapperArgumentGetter?.Invoke();

                    switch (mapperResult)
                    {
                        case Expression mapperResultExpression:
                            CheckRecursion(mapperResultExpression);
                            expression = mapperResultExpression;
                            break;
                        case IMapperExpressionProvider mapperResultExpressionProvider:
                            CheckRecursion(mapperResultExpressionProvider);
                            expression = mapperResultExpressionProvider.Expression;
                            break;
                        default:
                            throw new InvalidOperationException("The mapper is invalid.");
                    }
                }
                else
                {
                    ResolveMapperOrThrow(node, out var mapper);
                    CheckRecursion(mapper);
                    expression = mapper.Expression;
                }

                // We get a lambda expression from the mapper:
                //  y => new Something { Cat = y.Cat }
                // Then grab the initial source: y
                var mapperExpression = (LambdaExpression)expression;
                var mapperInitialSource = mapperExpression.Parameters[0];

                // Now we replace y with x.Thing, in the body:
                //  new Something { Cat = y.Cat } -> new Something { Cat = x.Thing.Cat }
                var replacer = new ReplacerVisitor(mapperInitialSource, sourceArgument);
                var finalExpression = replacer.Replace(mapperExpression.Body);

                // Finally! No need to revisit the expression,
                // as the mapper should already unwrap the expression by itself.
                return finalExpression;
            }

            return base.VisitMethodCall(node);
        }
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member

        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        private void CheckRecursion(object actualMapper)
        {
            if (actualMapper == _currentContext.Mapper)
                throw new InvalidOperationException("Cannot recursively include the same mapper.");
        }

        private void ResolveMapperOrThrow(MethodCallExpression node, out IMapperExpressionProvider mapper)
        {
            foreach (var mapperResolver in _mapperResolvers)
            {
                var result = mapperResolver.ResolveMapper(node);
                if (result == null) continue;
                mapper = result;
                return;
            }

            throw new InvalidOperationException($"Unable to resolve the mapper for the expression \"{node}\" ");
        }
    }
}