﻿// <copyright file="IWebDavDispatcher.cs" company="Fubar Development Junker">
// Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>

using System.Collections.Generic;

using FubarDev.WebDavServer.Dispatchers;
using FubarDev.WebDavServer.Formatters;



namespace FubarDev.WebDavServer
{
    /// <summary>
    /// The interface of a WebDAV server implementation
    /// </summary>
    public interface IWebDavDispatcher
    {
        /// <summary>
        /// Gets the list of supported WebDAV classes
        /// </summary>
        
        
        IReadOnlyCollection<IWebDavClass> SupportedClasses { get; }

        /// <summary>
        /// Gets the formatter for the WebDAV XML responses
        /// </summary>
        
        IWebDavOutputFormatter Formatter { get; }

        /// <summary>
        /// Gets the WebDAV class 1 implementation
        /// </summary>
        
        IWebDavClass1 Class1 { get; }

        /// <summary>
        /// Gets the WebDAV class 2 implementation
        /// </summary>
        
        IWebDavClass2 Class2 { get; }
    }
}
