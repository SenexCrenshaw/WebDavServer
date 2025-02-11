﻿// <copyright file="IDeleteHandler.cs" company="Fubar Development Junker">
// Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>

using System.Threading;
using System.Threading.Tasks;



namespace FubarDev.WebDavServer.Handlers
{
    /// <summary>
    /// Interface for the <c>DELETE</c> handler
    /// </summary>
    public interface IDeleteHandler : IClass1Handler
    {
        /// <summary>
        /// Deletes the element at the given path
        /// </summary>
        /// <param name="path">The path to the element to delete</param>
        /// <param name="cancellationToken">The cancellcation token</param>
        /// <returns>The result of the operation</returns>
        
        
        Task<IWebDavResult> DeleteAsync( string path, CancellationToken cancellationToken);
    }
}
