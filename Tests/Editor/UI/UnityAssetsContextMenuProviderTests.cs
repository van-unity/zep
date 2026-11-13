using System;
using System.Reflection;
using System.Text.RegularExpressions;
using Zep.Editor.UI.ContextMenu;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Zep.Editor.UI.Tests
{
    public sealed class UnityAssetsContextMenuProviderTests
    {
        [TestCase("Assets", "Assets/Create/Folder", "Assets/Create")]
        [TestCase("Assets/Create", "Assets/Create/Folder", "Assets/Create/Folder")]
        [TestCase("Assets/Create", "Assets/Create/Rendering/URP Asset", "Assets/Create/Rendering")]
        public void GetImmediateChildMenuPath_FoldsNestedCommandsIntoImmediateChildren(string parentPath, string rawPath, string expectedPath)
        {
            var providerType = typeof(ZepShellView).Assembly.GetType("Zep.Editor.UI.ContextMenu.UnityAssetsContextMenuProvider");
            Assert.That(providerType, Is.Not.Null);

            var method = providerType.GetMethod(
                "GetImmediateChildMenuPath",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.That(method, Is.Not.Null);

            var result = method.Invoke(null, new object[] { parentPath, rawPath }) as string;

            Assert.That(result, Is.EqualTo(expectedPath));
        }

        [Test]
        public void ReflectionFailure_LogsOnceAndDisablesProviderInstance()
        {
            var providerType = typeof(UnityAssetsContextMenuProvider);
            var failureMethod = providerType.GetMethod(
                "DisableAfterReflectionFailure",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var loggedField = providerType.GetField(
                "_reflectionFailureLogged",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(failureMethod, Is.Not.Null);
            Assert.That(loggedField, Is.Not.Null);

            var previousLogged = (bool)loggedField.GetValue(null);
            try
            {
                loggedField.SetValue(null, false);
                var provider = new UnityAssetsContextMenuProvider();
                LogAssert.Expect(
                    LogType.Warning,
                    new Regex("ContextMenu: UnityEditor\\.Menu reflection failed during test operation"));

                failureMethod.Invoke(
                    provider,
                    new object[] { "test operation", new InvalidOperationException("first") });
                failureMethod.Invoke(
                    provider,
                    new object[] { "test operation", new InvalidOperationException("second") });

                Assert.That(provider.IsAvailable, Is.False);
            }
            finally
            {
                loggedField.SetValue(null, previousLogged);
            }
        }
    }
}
