import type { UmbEntryPointOnInit, UmbEntryPointOnUnload } from "@umbraco-cms/backoffice/extension-api";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import { client } from "../api/client.gen.js";

export const onInit: UmbEntryPointOnInit = async (host, _extensionRegistry) => {
  // Wire the generated API client into the backoffice auth context: base URL, credentials,
  // token refresh and the default error handling. Resolved here so it is ready before any element runs.
  const authContext = await host.getContext(UMB_AUTH_CONTEXT);
  if (!authContext) {
    console.warn("UMB_AUTH_CONTEXT not available: the Blue Billywig API client will not be authenticated");
    return;
  }
  authContext.configureClient(client);
};

export const onUnload: UmbEntryPointOnUnload = (_host, _extensionRegistry) => {};
