"""Read-only check of every GET endpoint on a live API, plus sign-in, token refresh and sign-out.

Writes nothing except the admin's own session rows. Prompts for the password; nothing is stored.

    python smoke_prod.py                       # https://glazion-api-prod-production.up.railway.app, user admin
    python smoke_prod.py https://... someadmin
"""
import getpass
import os
import sys

import requests

BASE = (sys.argv[1] if len(sys.argv) > 1 else "https://glazion-api-prod-production.up.railway.app").rstrip("/")
USER = sys.argv[2] if len(sys.argv) > 2 else "admin"
results = []


def check(label, method, path, token=None, expect=(200,), **kwargs):
    headers = {"Authorization": f"Bearer {token}"} if token else {}
    try:
        r = requests.request(method, BASE + path, headers=headers, timeout=30, **kwargs)
        status, data = r.status_code, (r.json() if r.content and "json" in r.headers.get("content-type", "") else None)
    except requests.RequestException as ex:
        status, data = f"error {ex.__class__.__name__}", None
    results.append((status in expect, method, path, status, label))
    return data


def first(data, key="id"):
    items = data if isinstance(data, list) else (data or {}).get("items") or []
    return items[0].get(key) if items else None


check("health", "GET", "/health")
check("no token is refused", "GET", "/api/v2/admin/users", expect=(401,))
password = os.environ.get("GLAZION_ADMIN_PASSWORD") or getpass.getpass(f"Password for {USER}: ")
login = check("admin sign-in", "POST", "/api/v2/auth/login", json={"username": USER, "password": password}) or {}
password = None
tokens = login.get("tokens") or {}
token = tokens.get("accessToken")
if not token:
    print("Sign-in failed; check the username and password.")
    sys.exit(1)

check("me", "GET", "/api/v2/me", token)
check("overview", "GET", "/api/v2/admin/overview", token)
users = check("users", "GET", "/api/v2/admin/users", token)
if first(users):
    check("user by id", "GET", f"/api/v2/admin/users/{first(users)}", token)
recipes = check("recipes", "GET", "/api/v2/admin/recipes", token)
if first(recipes):
    check("recipe by id", "GET", f"/api/v2/admin/recipes/{first(recipes)}", token)
if first(recipes, "code"):
    check("recipe by lab code", "GET", f"/api/v2/admin/recipes/by-code/{first(recipes, 'code')}", token)
bases = check("base glazes", "GET", "/api/v2/admin/base-glazes", token)
if first(bases, "code"):
    check("base glaze by code", "GET", f"/api/v2/admin/base-glazes/{first(bases, 'code')}", token)
codes = check("capture codes", "GET", "/api/v2/admin/capture-codes", token)
code_items = codes.get("items") if isinstance(codes, dict) else codes
results.append((not code_items or all("recipeCount" in c for c in code_items), "CHECK", "capture codes carry recipeCount", "", ""))
if first(codes):
    check("capture code by id", "GET", f"/api/v2/admin/capture-codes/{first(codes)}", token)
    check("capture code recipes", "GET", f"/api/v2/admin/capture-codes/{first(codes)}/recipes", token)
uploads = check("admin uploads (needs R2 settings)", "GET", "/api/v2/admin/uploads", token)
up_items = uploads.get("items") if isinstance(uploads, dict) else uploads
results.append((not up_items or all("recipeId" in u and "recipeName" in u for u in up_items), "CHECK", "uploads carry recipeId/recipeName", "", ""))
images = check("images", "GET", "/api/v2/admin/images", token)
if first(images):
    check("image by id", "GET", f"/api/v2/admin/images/{first(images)}", token)
imports = check("imports", "GET", "/api/v2/admin/imports", token)
if first(imports):
    check("import by id", "GET", f"/api/v2/admin/imports/{first(imports)}", token)
    check("import records", "GET", f"/api/v2/admin/imports/{first(imports)}/records", token)
check("index status", "GET", "/api/v2/admin/index/status", token)
check("jobs", "GET", "/api/v2/admin/jobs", token)
check("materials", "GET", "/api/v2/materials?q=silica&limit=5", token)
check("UMF limits", "GET", "/api/v2/umf/limits?cone=6", token)
check("UMF calculate (no data written)", "POST", "/api/v2/umf/calculate", token,
      json={"rows": [{"material": "Custer Feldspar", "amount": 40}, {"material": "Silica", "amount": 30}, {"material": "Wollastonite", "amount": 30}], "cone": "6"})
check("my uploads", "GET", "/api/v2/uploads", token)
check("my recipes", "GET", "/api/v2/me/recipes", token)
check("my base glazes", "GET", "/api/v2/me/base-glazes", token, expect=(200, 400, 403, 409))

refreshed = check("refresh token", "POST", "/api/v2/auth/refresh", json={"refreshToken": tokens.get("refreshToken")}) or {}
check("sign out", "POST", "/api/v2/auth/logout", refreshed.get("accessToken") or token, expect=(200, 204))

passed = sum(1 for r in results if r[0])
for ok, method, path, status, label in results:
    print(f"{'PASS' if ok else 'FAIL'}  {method:5} {path:58} {status!s:>5}  {label}")
print(f"\n{passed}/{len(results)} passed")
sys.exit(0 if passed == len(results) else 1)
