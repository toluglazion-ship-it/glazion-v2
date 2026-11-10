"""End-to-end test of every Glazion API endpoint.

Creates its own participant, recipes, lab codes, capture codes, uploads and an import batch, so run it
against a local or staging copy, never production (it writes data and suspends the user it creates).

    GLAZION_API=http://localhost:5197 GLAZION_ADMIN_USER=admin GLAZION_ADMIN_PASSWORD=... python e2e_all_endpoints.py
"""
import hashlib
import io
import os
import random
import sys
import time
import uuid

import requests
from PIL import Image

BASE = os.environ.get("GLAZION_API", "http://localhost:5197").rstrip("/")
API = BASE + "/api/v2"
ADMIN_USER = os.environ.get("GLAZION_ADMIN_USER", "admin")
ADMIN_PASSWORD = os.environ["GLAZION_ADMIN_PASSWORD"]
SMALL_PHOTO = os.environ.get("GLAZION_SMALL_PHOTO")  # optional real photo below the 1200px minimum
RUN = uuid.uuid4().hex[:6]
def random_password():
    """A fresh password for this run's test users; mixed case, digits and a symbol to pass the API's rules."""
    return "E2e-" + uuid.uuid4().hex[:12] + "!"


PASSWORD = random_password()

results = []          # (ok, method, route, status, note)
covered = set()       # route templates exercised


def call(route, method="GET", path=None, token=None, expect=(200,), note="", **kwargs):
    """Call path (defaults to route), record whether the status is in expect, return the response."""
    url = BASE + "/" + (path or route).lstrip("/")
    headers = kwargs.pop("headers", {})
    if token:
        headers["Authorization"] = f"Bearer {token}"
    try:
        response = requests.request(method, url, headers=headers, timeout=60, **kwargs)
        status = response.status_code
    except requests.RequestException as ex:
        response, status = None, f"error {ex.__class__.__name__}"
    ok = status in expect
    detail = ""
    if not ok and response is not None:
        detail = response.text[:300].replace("\n", " ")
    results.append((ok, method, route, status, note + ((" | " + detail) if detail else "")))
    covered.add((method, route))
    return response


def body(response, default=None):
    try:
        return response.json()
    except Exception:
        return default


def items_of(data):
    """Paged responses carry items; some list endpoints return a bare array."""
    if isinstance(data, list):
        return data
    return (data or {}).get("items") or []


def poll(fetch, done, seconds=90):
    """Re-run fetch() until done(value) or timeout; returns the last value."""
    value = None
    for _ in range(seconds):
        value = fetch()
        if done(value):
            return value
        time.sleep(1)
    return value


def tile_image(width=1600, height=1600):
    """A textured, evenly lit glaze-like tile that passes the quality checks (sharpness, exposure)."""
    rng = random.Random(42)
    img = Image.new("RGB", (width, height))
    px = img.load()
    for y in range(height):
        for x in range(width):
            base = 110 + (x * 40) // width + (y * 30) // height
            n = rng.randint(-45, 45)
            px[x, y] = (min(225, max(40, base + n)), min(225, max(40, base + 20 + n)), min(225, max(40, base - 10 + n)))
    buffer = io.BytesIO()
    img.save(buffer, "JPEG", quality=92)
    return buffer.getvalue()


def put_to_storage(presign, data):
    response = requests.put(presign["url"], data=data, headers=presign.get("headers") or {}, timeout=120)
    ok = response.status_code in (200, 204)
    results.append((ok, "PUT", "(storage) presigned URL", response.status_code, ""))
    return ok


def login(username, password, expect=(200,), note=""):
    r = call("api/v2/auth/login", "POST", json={"username": username, "password": password}, expect=expect, note=note)
    data = body(r, {}) or {}
    tokens = data.get("tokens") or {}
    return tokens.get("accessToken"), tokens.get("refreshToken"), data


# ------------------------------------------------------------------ health and auth
call("health")
call("api/v2/me", expect=(401,), note="no token")
call("api/v2/auth/login", "POST", json={"username": "nobody-" + RUN, "password": "wrong-password-1"}, expect=(401,), note="wrong password")
admin, admin_refresh, _ = login(ADMIN_USER, ADMIN_PASSWORD, note="admin")
if not admin:
    print("Admin sign-in failed; cannot continue.")
    sys.exit(1)
call("api/v2/me", token=admin, note="admin")

r = call("api/v2/auth/refresh", "POST", json={"refreshToken": admin_refresh}, note="rotate admin refresh token")
rotated = body(r, {}) or {}
if rotated.get("accessToken"):
    admin = rotated["accessToken"]

# Self-registration creates a Pending account that only reaches /me
reg_user = f"e2e_reg_{RUN}"
r = call("api/v2/auth/register", "POST", json={"displayName": "E2E Registrant", "username": reg_user,
                                               "email": f"{reg_user}@example.test", "password": PASSWORD}, expect=(200, 201))
reg_id = (body(r, {}) or {}).get("userId")
pending, _, _ = login(reg_user, PASSWORD, note="pending account")
call("api/v2/me", token=pending, note="pending account can read /me")
call("api/v2/me/capture-context", token=pending, expect=(403,), note="pending account blocked elsewhere")

# ------------------------------------------------------------------ admin: users
call("api/v2/admin/overview", token=admin)
call("api/v2/admin/users", token=admin)
participant_code = random.choice("EFGHJK") + f"{random.randint(10, 99)}"
p_user = f"e2e_p_{RUN}"
r = call("api/v2/admin/users", "POST", token=admin, expect=(200, 201),
         json={"username": p_user, "password": PASSWORD, "displayName": "E2E Participant",
               "participantCode": participant_code, "email": f"{p_user}@example.test"})
p_id = (body(r, {}) or {}).get("id")
call("api/v2/admin/users/{userId:guid}", path=f"api/v2/admin/users/{p_id}", token=admin)
call("api/v2/admin/users/{userId:guid}", "PATCH", path=f"api/v2/admin/users/{p_id}", token=admin,
     json={"displayName": "E2E Participant (renamed)"}, expect=(200, 204))
if reg_id:
    call("api/v2/admin/users/{userId:guid}/status", "PATCH", path=f"api/v2/admin/users/{reg_id}/status", token=admin,
         json={"status": 1}, expect=(200, 204), note="activate registrant")
    activated, _, _ = login(reg_user, PASSWORD, note="activated registrant")
    call("api/v2/me/capture-context", token=activated, expect=(404, 400, 409), note="active but no code yet")

participant, participant_refresh, _ = login(p_user, PASSWORD, note="participant")
call("api/v2/admin/users", token=participant, expect=(403,), note="participant blocked from admin")

# ------------------------------------------------------------------ admin: recipes
recipe_ids = []
for name in ("Celadon", "Tenmoku", "Shino"):
    r = call("api/v2/admin/recipes", "POST", token=admin, expect=(200, 201),
             json={"name": f"E2E {name} {RUN}", "description": "e2e", "recipeJson": '{"source":"e2e"}'})
    recipe_ids.append((body(r, {}) or {}).get("id"))
call("api/v2/admin/recipes", token=admin)
call("api/v2/admin/recipes/{recipeId:guid}", path=f"api/v2/admin/recipes/{recipe_ids[0]}", token=admin)
call("api/v2/admin/recipes/{recipeId:guid}", "PATCH", path=f"api/v2/admin/recipes/{recipe_ids[0]}", token=admin,
     json={"description": "updated by e2e"}, expect=(200, 204))

# ------------------------------------------------------------------ chemistry
formulation = [{"rawName": "Custer Feldspar", "amount": 30, "unit": "%"}, {"rawName": "Silica", "amount": 30, "unit": "%"},
               {"rawName": "Wollastonite", "amount": 20, "unit": "%"}, {"rawName": "Talc", "amount": 10, "unit": "%"},
               {"rawName": "Ferro Frit 3134", "amount": 10, "unit": "%"}]
call("api/v2/materials", path="api/v2/materials?q=silica&limit=5", token=participant)
call("api/v2/umf/limits", path="api/v2/umf/limits?cone=6", token=participant)
call("api/v2/umf/calculate", "POST", token=participant,
     json={"rows": [{"material": f["rawName"], "amount": f["amount"]} for f in formulation], "cone": "6"})

# ------------------------------------------------------------------ lab codes (admin and participant)
r = call("api/v2/admin/base-glazes", "POST", token=admin, expect=(200, 201),
         json={"participantCode": participant_code, "name": f"E2E Base {RUN}", "description": "e2e", "formulation": formulation})
admin_base = (body(r, {}) or {}).get("code")
call("api/v2/admin/base-glazes", token=admin)
call("api/v2/admin/base-glazes/{code}", path=f"api/v2/admin/base-glazes/{admin_base}", token=admin)
r = call("api/v2/admin/base-glazes/{code}/recipes", "POST", path=f"api/v2/admin/base-glazes/{admin_base}/recipes", token=admin,
         expect=(200, 201), json={"name": f"E2E Variation {RUN}", "additions": [{"rawName": "Rutile", "amount": 4, "unit": "%"}]})
coded = body(r, {}) or {}
coded_id, coded_code = coded.get("id"), coded.get("code")
call("api/v2/admin/recipes/by-code/{code}", path=f"api/v2/admin/recipes/by-code/{coded_code}", token=admin)
call("api/v2/admin/recipes/by-code/{code}", "PATCH", path=f"api/v2/admin/recipes/by-code/{coded_code}", token=admin,
     json={"outcomeNote": "glossy amber"}, expect=(200, 204))
call("api/v2/recipes/{recipeId:guid}/calculate", "POST", path=f"api/v2/recipes/{coded_id}/calculate", token=admin, expect=(200, 201))
call("api/v2/recipes/by-code/{code}/calculate", "POST", path=f"api/v2/recipes/by-code/{coded_code}/calculate", token=admin, expect=(200, 201))

r = call("api/v2/me/base-glazes", "POST", token=participant, expect=(200, 201),
         json={"name": f"E2E My Base {RUN}", "formulation": formulation})
my_base = (body(r, {}) or {}).get("code")
call("api/v2/me/base-glazes", token=participant)
call("api/v2/me/base-glazes/{code}", path=f"api/v2/me/base-glazes/{my_base}", token=participant)
r = call("api/v2/me/base-glazes/{code}/recipes", "POST", path=f"api/v2/me/base-glazes/{my_base}/recipes", token=participant,
         expect=(200, 201), json={"name": f"E2E My Variation {RUN}", "additions": [{"rawName": "Tin Oxide", "amount": 5, "unit": "%"}]})
my_coded_code = (body(r, {}) or {}).get("code")
call("api/v2/me/recipes", token=participant)
call("api/v2/me/recipes/by-code/{code}", path=f"api/v2/me/recipes/by-code/{my_coded_code}", token=participant)
call("api/v2/admin/base-glazes", token=participant, expect=(403,), note="participant blocked from admin lab codes")

# ------------------------------------------------------------------ capture codes and recipe assignment
r = call("api/v2/admin/capture-codes", "POST", token=admin, expect=(200, 201), json={"assignedUserId": p_id, "labelPrefix": "E2E"})
code = body(r, {}) or {}
code_id = code.get("id")
results.append((bool(code.get("plainCode")), "CHECK", "capture code plain text returned once", "", ""))
two = [recipe_ids[0], coded_id]
call("api/v2/admin/capture-codes/{codeId:guid}/recipes", "POST", path=f"api/v2/admin/capture-codes/{code_id}/recipes", token=admin,
     json={"recipeIds": two}, expect=(200, 204), note="assign 2")
call("api/v2/admin/capture-codes/{codeId:guid}/recipes", "POST", path=f"api/v2/admin/capture-codes/{code_id}/recipes", token=admin,
     json={"recipeIds": [recipe_ids[1]]}, expect=(200, 204), note="assign 1 more (additive)")
r = call("api/v2/admin/capture-codes/{codeId:guid}", path=f"api/v2/admin/capture-codes/{code_id}", token=admin)
results.append(((body(r, {}) or {}).get("recipeCount") == 3, "CHECK", "assignment adds (recipeCount 3)", (body(r, {}) or {}).get("recipeCount"), ""))
r = call("api/v2/admin/capture-codes/{codeId:guid}/recipes", path=f"api/v2/admin/capture-codes/{code_id}/recipes", token=admin)
results.append((len(body(r, []) or []) == 3, "CHECK", "code recipes lists 3", len(body(r, []) or []), ""))
call("api/v2/admin/capture-codes/{codeId:guid}/recipes/{recipeId:guid}", "DELETE",
     path=f"api/v2/admin/capture-codes/{code_id}/recipes/{recipe_ids[1]}", token=admin, expect=(204,))
r = call("api/v2/admin/capture-codes", path=f"api/v2/admin/capture-codes?assignedUserId={p_id}", token=admin)
items = items_of(body(r, {}))
results.append((bool(items) and items[0].get("recipeCount") == 2, "CHECK", "remove drops recipeCount to 2", items[0].get("recipeCount") if items else None, ""))

r = call("api/v2/admin/capture-codes", "POST", token=admin, expect=(200, 201), json={"assignedUserId": p_id})
spare_code = (body(r, {}) or {}).get("id")
call("api/v2/admin/capture-codes/{codeId:guid}/revoke", "POST", path=f"api/v2/admin/capture-codes/{spare_code}/revoke", token=admin, expect=(204,))

r = call("api/v2/me/capture-context", token=participant)
context = body(r, {}) or {}
results.append((len(context.get("recipes") or []) == 2, "CHECK", "capture context shows the code's 2 recipes", len(context.get("recipes") or []), ""))

# ------------------------------------------------------------------ uploads (participant) and review (admin)
tile = tile_image()
tile_sha = hashlib.sha256(tile).hexdigest()


def create_upload(name, data, recipe_id, expect=(200, 201), note=""):
    return call("api/v2/uploads", "POST", token=participant, expect=expect, note=note,
                json={"captureCodeId": context.get("codeId") or code_id, "fileName": name, "contentType": "image/jpeg" if name.endswith(".jpg") else "image/png",
                      "sizeBytes": len(data), "sha256": hashlib.sha256(data).hexdigest(), "recipeId": recipe_id})


create_upload("blocked.jpg", tile, recipe_ids[1], expect=(400,), note="recipe removed from code is rejected")
r = create_upload("tile.jpg", tile, coded_id)
upload = body(r, {}) or {}
up_id = upload.get("id")
results.append((upload.get("recipeId") == coded_id and bool(upload.get("recipeName")), "CHECK", "upload returns recipeId and recipeName",
                upload.get("recipeName"), ""))
r = call("api/v2/uploads/{uploadId:guid}/presign", "POST", path=f"api/v2/uploads/{up_id}/presign", token=participant)
put_to_storage(body(r, {}) or {}, tile)
call("api/v2/uploads/{uploadId:guid}/complete", "POST", path=f"api/v2/uploads/{up_id}/complete", token=participant, json={"sha256": tile_sha})
done = poll(lambda: body(requests.get(f"{API}/uploads/{up_id}", headers={"Authorization": f"Bearer {participant}"}, timeout=30), {}),
            lambda u: (u or {}).get("status") in (4, 5, 8, 9))
results.append(((done or {}).get("status") == 4, "CHECK", "worker validated the tile (NeedsReview)", (done or {}).get("status"), ""))
call("api/v2/uploads/{uploadId:guid}", path=f"api/v2/uploads/{up_id}", token=participant)
call("api/v2/uploads", token=participant)

r = call("api/v2/admin/uploads", path="api/v2/admin/uploads?status=4", token=admin)
row = next((u for u in items_of(body(r, {})) if u.get("id") == up_id), {})
results.append((row.get("recipeId") == coded_id and bool(row.get("recipeName")), "CHECK", "admin upload shows recipe", row.get("recipeName"), ""))
image_id = row.get("imageId")
call("api/v2/admin/images", token=admin)
call("api/v2/admin/images/{imageId:guid}", path=f"api/v2/admin/images/{image_id}", token=admin)
call("api/v2/admin/uploads/{uploadId:guid}/approve", "POST", path=f"api/v2/admin/uploads/{up_id}/approve", token=admin,
     json={"note": "approved by e2e"}, expect=(204,))
done = poll(lambda: body(requests.get(f"{API}/uploads/{up_id}", headers={"Authorization": f"Bearer {participant}"}, timeout=30), {}),
            lambda u: (u or {}).get("status") in (8, 9))
results.append(((done or {}).get("status") == 8, "CHECK", "approved upload became Indexed", (done or {}).get("status"), ""))

# A real photo below the 1200px minimum, then admin rejection
if SMALL_PHOTO and os.path.exists(SMALL_PHOTO):
    small = open(SMALL_PHOTO, "rb").read()
    r = create_upload(os.path.basename(SMALL_PHOTO).replace(" ", "_"), small, recipe_ids[0], note="real photo, too small")
    small_id = (body(r, {}) or {}).get("id")
    r = call("api/v2/uploads/{uploadId:guid}/presign", "POST", path=f"api/v2/uploads/{small_id}/presign", token=participant)
    put_to_storage(body(r, {}) or {}, small)
    call("api/v2/uploads/{uploadId:guid}/complete", "POST", path=f"api/v2/uploads/{small_id}/complete", token=participant)
    done = poll(lambda: body(requests.get(f"{API}/uploads/{small_id}", headers={"Authorization": f"Bearer {participant}"}, timeout=30), {}),
                lambda u: (u or {}).get("status") in (4, 6, 9))
    results.append(((done or {}).get("status") in (4, 9), "CHECK", "small photo flagged (NeedsReview or Failed)", (done or {}).get("status"), ""))
    call("api/v2/admin/uploads/{uploadId:guid}/reject", "POST", path=f"api/v2/admin/uploads/{small_id}/reject", token=admin,
         json={"reason": "below 1200px"}, expect=(204, 409))

r = create_upload("cancel.jpg", tile, recipe_ids[0], note="to cancel")
cancel_id = (body(r, {}) or {}).get("id")
call("api/v2/uploads/{uploadId:guid}/cancel", "POST", path=f"api/v2/uploads/{cancel_id}/cancel", token=participant, expect=(200, 204))
call("api/v2/admin/uploads", token=participant, expect=(403,), note="participant blocked from admin uploads")

# ------------------------------------------------------------------ imports (admin)
r = call("api/v2/admin/imports", "POST", token=admin, expect=(200, 201),
         json={"name": f"E2E import {RUN}", "sourceLabel": "e2e", "defaultTileVariantKey": "white", "autoApprove": False})
batch_id = (body(r, {}) or {}).get("id")
call("api/v2/admin/imports", token=admin)
call("api/v2/admin/imports/{batchId:guid}", path=f"api/v2/admin/imports/{batch_id}", token=admin)
import_tile = tile_image(1500, 1500)
ext_id = f"e2e-{RUN}-1"
r = call("api/v2/admin/imports/{batchId:guid}/records", "POST", path=f"api/v2/admin/imports/{batch_id}/records", token=admin,
         json={"records": [{"externalRecordId": ext_id, "fileName": "import.jpg", "contentType": "image/jpeg", "sizeBytes": len(import_tile),
                            "sha256": hashlib.sha256(import_tile).hexdigest(), "tileVariantKey": "white", "coneMin": 6, "coneMax": 6,
                            "atmosphere": "oxidation", "labels": {"surface": "glossy"}, "recipeId": coded_id}]})
reg = ((body(r, {}) or {}).get("records") or [{}])[0]
if reg.get("upload"):
    put_to_storage(reg["upload"], import_tile)
call("api/v2/admin/imports/{batchId:guid}/records/complete", "POST", path=f"api/v2/admin/imports/{batch_id}/records/complete", token=admin,
     json={"externalRecordIds": [ext_id]})
record = poll(lambda: (items_of(body(requests.get(f"{API}/admin/imports/{batch_id}/records", headers={"Authorization": f"Bearer {admin}"}, timeout=30), {})) or [{}])[0],
              lambda rec: (rec or {}).get("status") in ("Validated", "NeedsReview", "Failed"))
call("api/v2/admin/imports/{batchId:guid}/records", path=f"api/v2/admin/imports/{batch_id}/records", token=admin)
import_image = (record or {}).get("imageId")
results.append((bool(import_image), "CHECK", "import record validated into an image", (record or {}).get("status"), ""))
call("api/v2/admin/images/{imageId:guid}/approve", "POST", path=f"api/v2/admin/images/{import_image}/approve", token=admin,
     json={"note": "import approved"}, expect=(204,))
call("api/v2/admin/imports/{batchId:guid}/close", "POST", path=f"api/v2/admin/imports/{batch_id}/close", token=admin, expect=(200, 204))

# A second import image, rejected through the images endpoint
ext_2 = f"e2e-{RUN}-2"
tile_2 = tile_image(1400, 1400)
r = call("api/v2/admin/imports", "POST", token=admin, expect=(200, 201), json={"name": f"E2E import {RUN} b", "autoApprove": False})
batch_2 = (body(r, {}) or {}).get("id")
r = call("api/v2/admin/imports/{batchId:guid}/records", "POST", path=f"api/v2/admin/imports/{batch_2}/records", token=admin,
         json={"records": [{"externalRecordId": ext_2, "fileName": "reject.jpg", "contentType": "image/jpeg", "sizeBytes": len(tile_2),
                            "tileVariantKey": "white", "coneMin": 6, "coneMax": 6, "atmosphere": "oxidation", "recipeId": recipe_ids[0]}]})
reg2 = ((body(r, {}) or {}).get("records") or [{}])[0]
if reg2.get("upload"):
    put_to_storage(reg2["upload"], tile_2)
call("api/v2/admin/imports/{batchId:guid}/records/complete", "POST", path=f"api/v2/admin/imports/{batch_2}/records/complete", token=admin,
     json={"externalRecordIds": [ext_2]})
rec2 = poll(lambda: (items_of(body(requests.get(f"{API}/admin/imports/{batch_2}/records", headers={"Authorization": f"Bearer {admin}"}, timeout=30), {})) or [{}])[0],
            lambda rec: (rec or {}).get("status") in ("Validated", "NeedsReview", "Failed"))
call("api/v2/admin/images/{imageId:guid}/reject", "POST", path=f"api/v2/admin/images/{(rec2 or {}).get('imageId')}/reject", token=admin,
     json={"reason": "e2e reject"}, expect=(204,))

# ------------------------------------------------------------------ derivatives, index, jobs
call("api/v2/admin/images/derivatives/backfill", "POST", token=admin, json={"imageIds": [image_id], "regenerate": True}, expect=(200, 202))
call("api/v2/admin/index/status", token=admin)
call("api/v2/admin/index/rebuild", "POST", token=admin, json={"imageIds": [image_id], "includeNonApproved": False}, expect=(200, 202))
r = call("api/v2/admin/jobs", token=admin)
jobs = items_of(body(r, {}))
job_id = jobs[0]["id"] if jobs else str(uuid.uuid4())
call("api/v2/admin/jobs/{jobId:guid}/retry", "POST", path=f"api/v2/admin/jobs/{job_id}/retry", token=admin, expect=(200, 202, 204, 409),
     note="409 expected when the job is not failed")
call("api/v2/admin/jobs", token=participant, expect=(403,), note="participant blocked from jobs")

# ------------------------------------------------------------------ recipe-to-image search
time.sleep(3)  # let index jobs for the approved images finish
r = call("api/v2/recipe-image/search", "POST", token=admin, json={"query": {"type": "recipe", "recipeId": coded_id}, "topK": 5})
search = body(r, {}) or {}
search_id = search.get("searchId")
hits = search.get("items") or []
results.append((any(h.get("imageId") in (image_id, import_image) for h in hits), "CHECK", "search finds the approved tile", len(hits), ""))
call("api/v2/recipe-image/search/{searchId:guid}", path=f"api/v2/recipe-image/search/{search_id}", token=admin)
call("api/v2/recipe-image/feedback", "POST", token=admin, expect=(200, 201, 204),
     json={"searchId": search_id, "imageId": hits[0]["imageId"] if hits else image_id, "feedback": "relevant", "note": "e2e"})
call("api/v2/recipe-image/search", "POST", token=participant, expect=(200,), note="raw-recipe search as participant",
     json={"query": {"type": "raw_recipe", "materials": formulation}, "firing": {"coneMin": 5, "coneMax": 7}})

# ------------------------------------------------------------------ session rules
r = call("api/v2/auth/refresh", "POST", json={"refreshToken": participant_refresh}, note="participant rotates")
call("api/v2/auth/refresh", "POST", json={"refreshToken": participant_refresh}, expect=(401,), note="reusing an exchanged refresh token is refused")
call("api/v2/me", token=participant, expect=(401,), note="reuse revoked the participant's sessions")
participant, _, _ = login(p_user, PASSWORD, note="participant signs in again")
call("api/v2/auth/logout", "POST", token=participant, expect=(200, 204))
call("api/v2/me", token=participant, expect=(401,), note="logged-out token is refused")
call("api/v2/admin/users/{userId:guid}/status", "PATCH", path=f"api/v2/admin/users/{p_id}/status", token=admin,
     json={"status": 2}, expect=(200, 204), note="suspend participant")
login(p_user, PASSWORD, expect=(401, 403), note="suspended participant cannot sign in")

# ------------------------------------------------------------------ report
passed = sum(1 for r in results if r[0])
for ok, method, route, status, note in results:
    print(f"{'PASS' if ok else 'FAIL'}  {method:6} {route:62} {status!s:>5}  {note}")
print(f"\n{passed}/{len(results)} checks passed; {len(covered)} distinct endpoints exercised.")
sys.exit(0 if passed == len(results) else 1)
