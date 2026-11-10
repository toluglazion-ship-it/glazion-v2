"""Create (or find) the two Pinecone indexes that hold Glazion's image vectors, and print the Railway variables.

Pinecone fixes one dimension per index, so image chemistry (22 numbers) and image text (the embedding model's size)
need separate indexes. The knowledge index chat reads (e.g. glazeai-2) is not touched.

    PINECONE_API_KEY=... python create_pinecone_indexes.py                    # text dimension 1536 (OpenAI embeddings)
    PINECONE_API_KEY=... python create_pinecone_indexes.py --text-dimension 256   # if Embeddings stays on Hashing

Safe to re-run: an existing index with the right dimension is reused; one with the wrong dimension stops the script.
"""
import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request

API = "https://api.pinecone.io"
VERSION = "2025-01"


def call(method, path, key, body=None):
    req = urllib.request.Request(API + path, method=method, data=None if body is None else json.dumps(body).encode(),
                                 headers={"Api-Key": key, "X-Pinecone-API-Version": VERSION, "Content-Type": "application/json", "Accept": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status, json.loads(r.read() or b"null")
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read() or b"null")


def ensure(key, name, dimension, cloud, region):
    status, index = call("GET", f"/indexes/{name}", key)
    if status == 404:
        status, index = call("POST", "/indexes", key, {"name": name, "dimension": dimension, "metric": "cosine",
                                                        "spec": {"serverless": {"cloud": cloud, "region": region}},
                                                        "deletion_protection": "enabled"})
        if status not in (200, 201, 202):
            sys.exit(f"Could not create {name}: HTTP {status} {index}")
        print(f"created {name} (dimension {dimension}, cosine, {cloud}/{region})")
    elif status != 200:
        sys.exit(f"Could not read {name}: HTTP {status} {index}")
    elif index.get("dimension") != dimension or index.get("metric") != "cosine":
        sys.exit(f"{name} exists with dimension {index.get('dimension')} / metric {index.get('metric')}; expected {dimension} / cosine. Pick another name.")
    else:
        print(f"found {name} (dimension {dimension})")
    for _ in range(60):  # serverless indexes take a few seconds to become ready
        if (index.get("status") or {}).get("ready"):
            return index["host"]
        time.sleep(2)
        _, index = call("GET", f"/indexes/{name}", key)
    sys.exit(f"{name} did not become ready; check the Pinecone console.")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--chemistry-name", default="glazion-image-chemistry")
    p.add_argument("--text-name", default="glazion-image-text")
    p.add_argument("--text-dimension", type=int, default=1536, help="1536 for text-embedding-3-small, 256 for the Hashing provider")
    p.add_argument("--cloud", default="aws")
    p.add_argument("--region", default="us-east-1")
    a = p.parse_args()
    key = os.environ.get("PINECONE_API_KEY") or sys.exit("Set PINECONE_API_KEY.")

    chemistry = ensure(key, a.chemistry_name, 22, a.cloud, a.region)
    text = ensure(key, a.text_name, a.text_dimension, a.cloud, a.region)
    print("\nSet these on the Railway API service (the API key as a secret), then redeploy:\n")
    print("VectorIndex__Provider=Pinecone")
    print("VectorIndex__Pinecone__ApiKey=<the same Pinecone API key>")
    print(f"VectorIndex__Pinecone__ChemistryIndexHost={chemistry}")
    print(f"VectorIndex__Pinecone__TextIndexHost={text}")
    if a.text_dimension == 1536:
        print("Embeddings__Provider=OpenAI")
        print("Embeddings__OpenAI__ApiKey=<OpenAI API key>")
    print("\nOn start the API sends every approved image to the new indexes; GET /api/v2/admin/index/status shows the backlog draining.")


if __name__ == "__main__":
    main()
