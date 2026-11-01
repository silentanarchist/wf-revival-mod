"""Build the custom-card tester package from approved creator cards (claude/custom-bundle-plan.md, 2026-10-21).

usage: build_custom.py <out dir> <card json>... [--art DIR] [--version N]

Writes, for each platform (pc, android):
  custom-cards_<platform>.bundle   the RawCardScript objects (+ the card MonoScripts), one per card, keyed by a GUID
  custom-art_<platform>.bundle     one Texture2D + Sprite per card (RGBA32, 1024 x 1024), keyed by the card's art GUID
and custom-tester.json: {"version", "cards": [{"id", "name", "guid", "art", "shortId"}], "text": {id: {lang: {name, text, lore}}},
"files": {platform: {name: {"size", "sha256"}}}}.

Templates: the game's own allcards bundle (card objects and the two MonoScripts) and the Genestealer Cults art bundle
(textures and sprites), stripped to what is needed. Nothing in them is changed; the originals are read only.
A card's compiled layout is its creator record's "native" (CardCompiler on the server); {"$art": "upload:ID"} becomes an
official picture of the card's army (the mod shows the custom picture from the art bundle), {"$ref": id} references to official cards are not supported yet (a custom card can only refer to custom cards
in the same package).
"""
import sys, os, json, copy, hashlib, struct, re
import UnityPy
from UnityPy.enums import TextureFormat
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
GAME = os.environ.get('WF_GAME', '/mnt/user-data/uploads/Warhammer 40k Warpforge')
CARDS_TEMPLATE = os.environ.get('WF_ALLCARDS', os.path.join(HERE, '..', 'build', 'allcards_assets_all.bundle'))
ART_TEMPLATE = os.environ.get('WF_ART_TEMPLATE', os.path.join(GAME, 'Warpforge_Data', 'StreamingAssets', 'aa', 'StandaloneWindows64',
                                                              'genestealercultscardassets_assets_all.bundle'))
ANDROID = 13
SHORT_ID_BASE = 90000          # cardShortId range reserved for custom cards (official ones end far below)
LANGS = ['en', 'cs', 'de', 'es', 'fr', 'it', 'ja', 'ko', 'pl', 'pt-BR', 'ru', 'tr', 'zh-CN']


# the creator site's **Keyword** becomes the game's [[keyword]] (the keyword's name and icon in the card text, as the
# game writes its own cards; build_set.py's list); other **bold** is left as plain text
KEYWORDS = {'Hunt Mark': 'huntMark', 'Blood Thirst': 'bloodThirst', 'Ferocity': 'ferocity', 'Pack': 'pack', 'Rally': 'rally',
            'Camouflage': 'camouflage', 'Tide': 'tide', 'Flying': 'flying', 'Flank': 'flank', 'Vanguard': 'vanguard',
            'Sniper': 'sniper', 'Strike': 'strike', 'Slay': 'slay', 'Artifice': 'artifice', 'Shield': 'shield',
            'Invulnerable': 'invulnerable', 'Regeneration': 'regeneration', 'Blind': 'blind', 'Ephemeral': 'ephemeral',
            'Talent': 'talent', 'Armour': 'armour', 'Stealth': 'stealth', 'Stun': 'stun', 'Fast': 'fast',
            'Codex': 'codex', 'Melee': 'melee', 'Ranged': 'ranged', 'Agenda': 'agenda', 'Teleport': 'teleport', 'Oath': 'oath', 'Blast': 'blast'}


def markup(text):
    def bold(m):
        word = m.group(1).strip()
        key = KEYWORDS.get(word) or KEYWORDS.get(word.title())
        return '[[%s]]' % key if key else word
    return re.sub(r'\*\*([^*]+)\*\*', bold, text or '')


def guid_for(kind, uid):
    return hashlib.md5(('revival-%s:%s' % (kind, uid)).encode()).hexdigest()


def pid_for(kind, uid):
    return struct.unpack('<q', hashlib.md5(('revival-%s:%s' % (kind, uid)).encode()).digest()[:8])[0]


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def rename_cab(bf, kind, platform):
    """Unity refuses to open two bundles whose inner file has the same name, and the package's bundles are made from
    the game's own (allcards, a faction's art), which are loaded too: the inner file gets a name of its own."""
    old = [k for k, f in bf.files.items() if hasattr(f, 'objects')][0]
    new = 'CAB-' + hashlib.md5(('revival-%s-%s' % (kind, platform)).encode()).hexdigest()
    items = list(bf.files.items())
    bf.files.clear()
    for k, f in items:
        if k == old:
            k = new
            try:
                f.name = new
            except Exception:
                pass
        bf.files[k] = f
    return new


def serialized(env):
    bf = env.file
    for f in bf.files.values():
        if hasattr(f, 'objects'):
            return bf, f
    raise SystemExit('no serialized file in the template')


def fill_art(node, art_guids):
    """{"$art": "upload:ID"} -> the card's art GUID; {"$ref": x} is refused for now."""
    if isinstance(node, dict):
        if '$art' in node:
            key = node['$art']
            cid = key.split(':', 1)[1] if ':' in key else key
            if cid not in art_guids:
                raise SystemExit('no artwork for ' + cid)
            return {'m_AssetGUID': art_guids[cid], 'm_SubObjectName': '', 'm_SubObjectType': ''}
        if '$ref' in node:
            raise SystemExit('references to other cards are not supported yet: ' + str(node['$ref']))
        return {k: fill_art(v, art_guids) for k, v in node.items()}
    if isinstance(node, list):
        return [fill_art(v, art_guids) for v in node]
    return node


def zero(v):
    if isinstance(v, dict):
        return {k: zero(x) for k, x in v.items()}
    if isinstance(v, list):
        return []
    if isinstance(v, str):
        return ''
    if isinstance(v, float):
        return 0.0
    if isinstance(v, bytes):
        return b''
    return 0


def merge(template, value, sample=None):
    """The compiled layout laid over the template's shape, so every field the game's type tree has exists
    (a server-compiled layout can lack fields added to the game's layout later). Lists take the compiled
    list, each element shaped by the template list's first element when it has one."""
    if isinstance(template, dict):
        if not isinstance(value, dict):
            return zero(template)
        out = {}
        for k, t in template.items():
            out[k] = merge(t, value[k]) if k in value else zero(t)
        return out
    if isinstance(template, list):
        if not isinstance(value, list):
            return []
        shape = template[0] if template else (sample[0] if sample else None)
        return [merge(shape, v) if isinstance(shape, (dict, list)) else v for v in value]
    return value if value is not None else zero(template)


def build_cards(cards, art_guids, out, platform):
    env = UnityPy.load(CARDS_TEMPLATE)
    bf, sf = serialized(env)
    ab = [o for o in sf.objects.values() if o.type.name == 'AssetBundle'][0]
    abt = ab.read_typetree()
    template = None
    pictures = {}          # (army, card type) -> an official card's picture GUID, the custom card's stand-in picture
    for o in list(sf.objects.values()):
        if o.type.name == 'MonoBehaviour':
            t = o.read_typetree()
            if 'uniqueId' in t and 'cardAbilities' in t:
                g = (t.get('cardImageReference') or {}).get('m_AssetGUID')
                if g:
                    pictures.setdefault((t.get('cardArmy'), t.get('cardType')), g)
                    pictures.setdefault((t.get('cardArmy'), None), g)
                if template is None:
                    template = o
            if o is not template:
                del sf.objects[o.path_id]
    if template is None:
        raise SystemExit('no card object in the template')
    scripts = [o.path_id for o in sf.objects.values() if o.type.name == 'MonoScript']
    container, preload, built = [], [], []
    for c in cards:
        uid = c['id']
        # the card's picture reference names an official picture of its army, so every place in the game that preloads
        # it succeeds; the mod shows the custom picture from the art bundle (RawCardScript.cardSprite)
        n = c['native']
        stand_in = pictures.get((n.get('cardArmy'), n.get('cardType'))) or pictures.get((n.get('cardArmy'), None)) or next(iter(pictures.values()))
        layout = merge(template.read_typetree(), fill_art(copy.deepcopy(n), {k: stand_in for k in art_guids}))
        layout['uniqueId'] = uid
        layout['cardShortId'] = c['shortId']
        layout['m_Script'] = template.read_typetree()['m_Script']
        obj = copy.copy(template)
        obj.path_id = pid_for('card', uid)
        sf.objects[obj.path_id] = obj
        obj.save_typetree(layout)
        guid = guid_for('card', uid)
        pre = [{'m_FileID': 0, 'm_PathID': p} for p in scripts + [obj.path_id]]
        container.append((guid, {'preloadIndex': len(preload), 'preloadSize': len(pre), 'asset': {'m_FileID': 0, 'm_PathID': obj.path_id}}))
        preload += pre
        built.append((uid, guid, obj.path_id))
    del sf.objects[template.path_id]
    abt['m_Name'] = abt['m_AssetBundleName'] = 'custom-cards.bundle'
    abt['m_Container'] = sorted(container)
    abt['m_PreloadTable'] = preload
    abt['m_MainAsset'] = {'preloadIndex': 0, 'preloadSize': 0, 'asset': {'m_FileID': 0, 'm_PathID': 0}}
    ab.save_typetree(abt)
    if platform == 'android':
        sf._m_target_platform = ANDROID
    rename_cab(bf, 'cards', platform)
    data = bf.save(packer='lz4')
    path = os.path.join(out, 'custom-cards_%s.bundle' % platform)
    open(path, 'wb').write(data)
    return path, built


def build_art(cards, art_dir, out, platform):
    env = UnityPy.load(ART_TEMPLATE)
    bf, sf = serialized(env)
    ab = [o for o in sf.objects.values() if o.type.name == 'AssetBundle'][0]
    abt = ab.read_typetree()
    # the game's pictures come in two shapes: troops (and warlords) at 180 pixels per unit, their alpha a mask of where
    # the figure is drawn OVER the frame (almost all 0: the frame shows); stratagems at 360, opaque. A custom picture
    # takes the shape of its kind, or the card is drawn at the wrong size with the frame hidden under the picture.
    tex_t, spr_by_kind = None, {}
    for o in list(sf.objects.values()):
        if o.type.name == 'Texture2D' and tex_t is None:
            tex_t = o
        elif o.type.name == 'Sprite':
            name = o.read_typetree()['m_Name']
            kind = 'troop' if '_inf_' in name else 'stratagem' if '_strat_' in name else None
            if kind and kind not in spr_by_kind:
                spr_by_kind[kind] = o
    if set(spr_by_kind) != {'troop', 'stratagem'}:
        raise SystemExit('the art template has no troop or no stratagem picture')
    templates = {kind: o.read_typetree() for kind, o in spr_by_kind.items()}
    spr_t = spr_by_kind['troop']
    for o in list(sf.objects.values()):
        if o.type.name != 'AssetBundle' and o is not tex_t and o is not spr_t:
            del sf.objects[o.path_id]
    container, preload, built = [], [], []
    for c in cards:
        uid = c['id']
        png = os.path.join(art_dir, uid + '.png')
        if not os.path.isfile(png):
            raise SystemExit('no artwork file ' + png)
        img = Image.open(png).convert('RGBA')
        if img.size != (1024, 1024):
            img = img.resize((1024, 1024), Image.LANCZOS)
        kind = 'stratagem' if c.get('kind') == 'stratagem' else 'troop'
        if kind == 'troop':
            # where the figure is drawn over the frame: the card's own mask (<id>-mask.png, white = over the frame)
            # when there is one, otherwise nowhere
            mask_png = os.path.join(art_dir, uid + '-mask.png')
            if os.path.isfile(mask_png):
                mask = Image.open(mask_png).convert('RGBA').resize((1024, 1024), Image.LANCZOS).getchannel('A')
            else:
                mask = Image.new('L', (1024, 1024), 0)
            img.putalpha(mask)
        else:
            img.putalpha(255)
        tex = copy.copy(tex_t)
        tex.path_id = pid_for('tex', uid)
        sf.objects[tex.path_id] = tex
        t = tex.read()
        t.m_Name = 'Custom_' + uid
        t.set_image(img, target_format=TextureFormat.RGBA32, mipmap_count=1)
        t.save()
        spr = copy.copy(spr_t)
        spr.path_id = pid_for('spr', uid)
        sf.objects[spr.path_id] = spr
        s = copy.deepcopy(templates[kind])
        s['m_Name'] = 'Custom_' + uid
        s['m_Rect'] = {'x': 0.0, 'y': 0.0, 'width': 1024.0, 'height': 1024.0}
        s['m_RD']['texture'] = {'m_FileID': 0, 'm_PathID': tex.path_id}
        s['m_RD']['alphaTexture'] = {'m_FileID': 0, 'm_PathID': 0}
        s['m_RD']['textureRect'] = {'x': 0.0, 'y': 0.0, 'width': 1024.0, 'height': 1024.0}
        s['m_RD']['textureRectOffset'] = {'x': 0.0, 'y': 0.0}
        spr.save_typetree(s)
        guid = guid_for('art', uid)
        for pid in (tex.path_id, spr.path_id):
            container.append((guid, {'preloadIndex': len(preload), 'preloadSize': 2, 'asset': {'m_FileID': 0, 'm_PathID': pid}}))
        preload += [{'m_FileID': 0, 'm_PathID': tex.path_id}, {'m_FileID': 0, 'm_PathID': spr.path_id}]
        built.append((uid, guid, tex.path_id, spr.path_id))
    del sf.objects[tex_t.path_id]
    del sf.objects[spr_t.path_id]
    abt['m_Name'] = abt['m_AssetBundleName'] = 'custom-art.bundle'
    abt['m_Container'] = sorted(container, key=lambda kv: (kv[0], kv[1]['asset']['m_PathID']))
    abt['m_PreloadTable'] = preload
    abt['m_MainAsset'] = {'preloadIndex': 0, 'preloadSize': 0, 'asset': {'m_FileID': 0, 'm_PathID': 0}}
    ab.save_typetree(abt)
    # the template's streamed picture data file is no longer referred to
    for name in list(bf.files.keys()):
        if name.endswith('.resS') or name.endswith('.resource'):      # the template's streamed pictures and sounds
            del bf.files[name]
    if platform == 'android':
        sf._m_target_platform = ANDROID
    rename_cab(bf, 'art', platform)
    data = bf.save(packer='lz4')
    path = os.path.join(out, 'custom-art_%s.bundle' % platform)
    open(path, 'wb').write(data)
    return path, built


def verify(path, want_types):
    env = UnityPy.load(path)
    objs = {o.path_id: o for o in env.objects}
    kinds = {}
    for o in objs.values():
        kinds[o.type.name] = kinds.get(o.type.name, 0) + 1
    abt = [o for o in objs.values() if o.type.name == 'AssetBundle'][0].read_typetree()
    dangling = []
    for key, e in abt['m_Container']:
        if e['asset']['m_PathID'] not in objs:
            dangling.append(key)
        for p in abt['m_PreloadTable'][e['preloadIndex']:e['preloadIndex'] + e['preloadSize']]:
            if p['m_PathID'] not in objs:
                dangling.append(key)
    for f in env.file.files.values():
        if hasattr(f, 'objects'):
            plat = f.target_platform
    names = [k for k in env.file.files.keys()]
    print('  %s: %s, platform %s, container %d, dangling %d, %d bytes, inner %s' % (os.path.basename(path), kinds, plat, len(abt['m_Container']), len(dangling), os.path.getsize(path), names))
    for t in want_types:
        assert t in kinds, t
    assert not dangling


def main():
    args = sys.argv[1:]
    art_dir, version = None, 1
    if '--art' in args:
        i = args.index('--art'); art_dir = args[i + 1]; del args[i:i + 2]
    if '--version' in args:
        i = args.index('--version'); version = int(args[i + 1]); del args[i:i + 2]
    out, files = args[0], args[1:]
    os.makedirs(out, exist_ok=True)
    cards = []
    for n, f in enumerate(files):
        c = json.load(open(f, encoding='utf-8'))
        if c.get('status') != 'approved':
            print('skipped (not approved):', f); continue
        if c.get('updates'):
            # an update to an official card (compiled with that card's own id): a patch, not a new card - not built here yet
            print('skipped (update to official card %s, a patch):' % c['updates'], f); continue
        if not isinstance(c.get('native'), dict):
            raise SystemExit('no compiled layout in ' + f)
        cards.append({'id': c['id'], 'name': c.get('name', ''), 'text': c.get('text', ''), 'lore': c.get('lore', ''),
                      'native': c['native'], 'shortId': SHORT_ID_BASE + int(c['id'].split('-')[-1])})
    if not cards:
        raise SystemExit('no approved cards')
    art_dir = art_dir or os.path.join(os.path.dirname(files[0]), 'art')
    art_guids = {c['id']: guid_for('art', c['id']) for c in cards}
    manifest = {'version': version, 'cards': [], 'text': {}, 'files': {}}
    for c in cards:
        manifest['cards'].append({'id': c['id'], 'name': c['name'], 'guid': guid_for('card', c['id']), 'art': art_guids[c['id']], 'shortId': c['shortId'],
                                  'rarity': c['native'].get('cardRarity', 1), 'army': c['native'].get('cardArmy', 0)})
        # English from the design; other languages fall back to it until the creator site carries translations
        manifest['text'][c['id']] = {'en': {'name': c['name'], 'text': markup(c['text']), 'lore': c['lore']}}
    for platform in ('pc', 'android'):
        p1, _ = build_cards(cards, art_guids, out, platform)
        p2, _ = build_art(cards, art_dir, out, platform)
        manifest['files'][platform] = {os.path.basename(p): {'size': os.path.getsize(p), 'sha256': sha256(p)} for p in (p1, p2)}
        print(platform + ':')
        verify(p1, ('MonoBehaviour', 'MonoScript'))
        verify(p2, ('Texture2D', 'Sprite'))
    json.dump(manifest, open(os.path.join(out, 'custom-tester.json'), 'w', encoding='utf-8'), indent=1, ensure_ascii=False)
    print('cards:', ', '.join('%s (%s)' % (c['id'], c['name']) for c in cards))


if __name__ == '__main__':
    main()
