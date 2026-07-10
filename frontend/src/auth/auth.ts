import {
  PublicClientApplication,
  type AccountInfo,
  type Configuration
} from "@azure/msal-browser";

// Azure AD app registration (non-secret IDs, safe to keep in source).
// Two-app setup: the SPA app is used for sign-in; tokens are requested for the API app's scope.
const AAD_SPA_CLIENT_ID = "34352002-2bb3-4071-869d-512d550c749e";
// The app registration supports "all Microsoft account users" (multitenant + MSA), so the
// sign-in authority must be a shared endpoint ("common") rather than a single tenant id,
// otherwise personal/other-tenant accounts fail with AADSTS50020.
const AAD_TENANT_ID = "common";
const AAD_API_SCOPE = "api://460d1cac-4151-40a5-8c60-d6fc3d53c4ea/access_as_user";

// Env vars override the defaults (e.g. set VITE_AAD_CLIENT_ID="" to force local dev mode).
const clientId = import.meta.env.VITE_AAD_CLIENT_ID?.trim() ?? AAD_SPA_CLIENT_ID;
const tenant = import.meta.env.VITE_AAD_TENANT?.trim() || AAD_TENANT_ID;
const apiScope = import.meta.env.VITE_API_SCOPE?.trim() || AAD_API_SCOPE;

/** When no AAD client id is configured we run in local dev mode (no login). */
export const authEnabled = Boolean(clientId);

const scopes = apiScope ? [apiScope] : ["User.Read"];

let msal: PublicClientApplication | null = null;

export async function initAuth(): Promise<void> {
  if (!authEnabled) return;

  const config: Configuration = {
    auth: {
      clientId: clientId!,
      authority: `https://login.microsoftonline.com/${tenant}`,
      redirectUri: window.location.origin
    },
    cache: { cacheLocation: "localStorage" },
    system: {
      // Refresh the access token this many seconds before it actually expires,
      // so a request never goes out with a token about to lapse (avoids 401s).
      tokenRenewalOffsetSeconds: 600
    }
  };
  msal = new PublicClientApplication(config);
  await msal.initialize();
}

export function currentAccount(): AccountInfo | null {
  return msal?.getAllAccounts()[0] ?? null;
}

export async function login(): Promise<void> {
  if (!msal) return;
  await msal.loginPopup({ scopes });
}

export function logout(): void {
  if (!msal) return;
  const account = currentAccount() ?? undefined;
  void msal.logoutPopup({ account });
}

export async function getToken(): Promise<string | null> {
  if (!msal) return null;
  const account = currentAccount();
  if (!account) return null;
  try {
    const result = await msal.acquireTokenSilent({ account, scopes });
    // Proactively renew if the cached token is within 5 minutes of expiring, so
    // long-running actions (e.g. an SSE stream) don't fail mid-flight on a 401.
    if (isNearExpiry(result.expiresOn)) {
      const refreshed = await msal.acquireTokenSilent({ account, scopes, forceRefresh: true });
      return refreshed.accessToken;
    }
    return result.accessToken;
  } catch {
    // Silent renewal failed (e.g. refresh token expired) — fall back to interactive.
    const result = await msal.acquireTokenPopup({ scopes });
    return result.accessToken;
  }
}

function isNearExpiry(expiresOn: Date | null): boolean {
  if (!expiresOn) return false;
  const fiveMinutes = 5 * 60 * 1000;
  return expiresOn.getTime() - Date.now() < fiveMinutes;
}
