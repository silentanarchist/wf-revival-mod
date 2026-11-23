"""Build the custom-card tester package from approved creator cards (claude/custom-bundle-plan.md, 2026-10-21).

usage: build_custom.py <out dir> <card json>... [--art DIR] [--version N]

Writes, for each platform (pc, android):
  custom-cards_<platform>.bundle   the RawCardScript objects (+ the card MonoScripts), one per card, keyed by a GUID
  custom-art_<platform>.bundle     one Texture2D + Sprite per card (RGBA32, 1024 x 1024), keyed by the card's art GUID
and custom-tester.json: {"version", "cards": [{"id", "name", "guid", "art", "shortId"}], "text": {id: {lang: {name, text, lore}}},
"updates": [{"id", "design", "guid", "text": {lang: {text}}}], "files": {platform: {name: {"size", "sha256"}}}}.

Updates to official cards (approved designs with "updates", claude/official-card-updates.md; 2026-11-23): each is built
from the official card itself (the card template bundle is the game's allcards, so it is there), with only its rules
fields taken from what the design changed (UPDATE_FIELDS, compared with the official card compiled the same way:
WF_SERVER + WF_CONTENT, see official_compiled). The manifest lists the fields it changed ("changed") and every other
card field ("official"): the mod copies the "official" fields from the official card as the game loaded it onto the
update object at start-up, so the update is a patch on the current official card, and a later fix in the game's own
card bundle carries through. The object goes into the cards bundle under its own key and is NOT added to the
collection: the mod plays it in place of the official card only in the testers' custom-card modes (CardUpdates.cs). References to
official cards (the card itself, a summoned unit: {"$ref": id} in the design, internal references in the official
card) point into the game's own allcards bundle through an external file entry (the bundle is loaded by then).

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
# the card template per platform: the game's allcards bundle as the server serves it (an update's references point into it,
# so it must be the copy the games load; the PC and Android files have the same inner name and object ids)
TEMPLATES = {'pc': CARDS_TEMPLATE, 'android': os.environ.get('WF_ALLCARDS_ANDROID', CARDS_TEMPLATE)}
ART_TEMPLATE = os.environ.get('WF_ART_TEMPLATE', os.path.join(GAME, 'Warpforge_Data', 'StreamingAssets', 'aa', 'StandaloneWindows64',
                                                              'genestealercultscardassets_assets_all.bundle'))
ANDROID = 13
SHORT_ID_BASE = 90000          # cardShortId range reserved for custom cards (official ones end far below)
# the rules of a card; everything else of an updated card stays the official card's (claude/official-card-updates.md)
UPDATE_FIELDS = ('manaCost', 'meleeAttack', 'rangeAttack', 'maxHealth', 'armour', 'keyword', 'CardTraits', 'ShowTraits',
                 'cardAbilities', 'activeAbility', 'alternativeAbility', 'hasActiveAbility', 'hasAlternativeAbility',
                 'choicesDefinition', 'targetsAvailableCounter', 'cardTargetCriteria', 'targeted', 'blockActionsWhileResolving')
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


def to_official(node, ext, pids):
    """An updated card's references into the game's allcards bundle: {"$ref": id} -> that card, and an internal reference
    copied from the official card (file 0 = allcards) -> the same object through the external entry `ext`."""
    if isinstance(node, dict):
        if '$ref' in node:
            rid = node['$ref']
            if rid not in pids:
                raise SystemExit('an update refers to a card that is not an official card: ' + str(rid))
            return {'m_FileID': ext, 'm_PathID': pids[rid]}
        if '$art' in node:
            raise SystemExit('an update cannot change a picture: ' + str(node['$art']))
        if set(node) == {'m_FileID', 'm_PathID'}:
            return {'m_FileID': ext, 'm_PathID': node['m_PathID']} if node['m_FileID'] == 0 and node['m_PathID'] else dict(node)
        return {k: to_official(v, ext, pids) for k, v in node.items()}
    if isinstance(node, list):
        return [to_official(v, ext, pids) for v in node]
    return node


def three_way(official, base, new):
    """The official card's value with only what the update changed: where the update's compiled value (`new`) equals the
    official design compiled the same way (`base`), the official value stays (with what the compiler does not write,
    such as an effect's animations); where it differs, the update's value. Lists of different length are compared item
    by item, and the update's extra items are taken as they are. Returns (value, changed)."""
    if json.dumps(new, sort_keys=True) == json.dumps(base, sort_keys=True):
        return official, False
    if isinstance(new, dict) and isinstance(base, dict) and isinstance(official, dict) and '$ref' not in new and '$ref' not in base:
        out, changed = dict(official), False
        for k, v in new.items():
            if k in official and k in base:
                out[k], c = three_way(official[k], base[k], v)
            else:
                out[k], c = v, True
            changed |= c
        return out, changed
    if isinstance(new, list) and isinstance(base, list) and isinstance(official, list):
        out = []
        for i, v in enumerate(new):
            if i < len(base) and i < len(official):
                out.append(three_way(official[i], base[i], v)[0])
            else:
                out.append(v)
        return out, True
    return new, True


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


def build_cards(cards, art_guids, out, platform, updates=()):
    env = UnityPy.load(TEMPLATES[platform])
    bf, sf = serialized(env)
    ab = [o for o in sf.objects.values() if o.type.name == 'AssetBundle'][0]
    abt = ab.read_typetree()
    template = None
    pictures = {}          # (army, card type) -> an official card's picture GUID, the custom card's stand-in picture
    official, pids = {}, {}     # official card id -> its layout / its object's path id in allcards (for updates)
    own_cab = [k for k, f in bf.files.items() if hasattr(f, 'objects')][0]
    for o in list(sf.objects.values()):
        if o.type.name == 'MonoBehaviour':
            t = o.read_typetree()
            if 'uniqueId' in t and 'cardAbilities' in t:
                pids[t['uniqueId']] = o.path_id
                if any(u['updates'] == t['uniqueId'] for u in updates):
                    official[t['uniqueId']] = t
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
    if updates:
        # the game's own allcards bundle as an external file of this one (Unity finds it loaded by its inner name)
        ext_entry = copy.copy(sf.externals[0])
        ext_entry.path = 'archive:/%s/%s' % (own_cab, own_cab)
        try:
            ext_entry.guid = b'\x00' * 16
        except Exception:
            pass
        ext_entry.type = 0
        sf.externals.append(ext_entry)
        ext = len(sf.externals)          # file ids count the external entries from 1
    for u in updates:
        oid = u['updates']
        if oid not in official:
            raise SystemExit('update %s: no official card %s in the card template' % (u['id'], oid))
        layout = to_official(copy.deepcopy(official[oid]), ext, pids)
        base = u.get('base')
        changed = []
        for k in UPDATE_FIELDS:      # only the rules come from the design (its picture is the official card's anyway)
            if k not in u['native'] or k not in official[oid]:
                continue
            if isinstance(base, dict) and k in base:
                # the official value, with only what the update changed (the official value's references are its own)
                value, c = three_way(official[oid][k], base[k], u['native'][k])
                if not c:
                    continue
                layout[k] = merge(official[oid][k], to_official(copy.deepcopy(value), ext, pids))
            else:
                # an update saved before 2026-11-23 has no compiled official version: its whole rules field is taken
                layout[k] = merge(official[oid][k], to_official(copy.deepcopy(u['native'][k]), ext, pids))
            changed.append(k)
        print('  update %s (%s): %s' % (oid, u['id'], ', '.join(changed) or 'nothing changed' + ('' if isinstance(base, dict) else ' (no compiled official card)')))
        # the patch: the mod takes these fields from the update and every other card field from the official card as the
        # game loaded it (so a later fix to the official card in the game's own card bundle carries through)
        u['changed'] = changed
        u['official'] = [k for k in official[oid] if not k.startswith('m_') and k not in changed and k != 'uniqueId']
        layout['m_Script'] = official[oid]['m_Script']
        layout['m_Name'] = official[oid].get('m_Name', '') + ' (update)'
        obj = copy.copy(template)
        obj.path_id = pid_for('update', oid)
        sf.objects[obj.path_id] = obj
        obj.save_typetree(layout)
        guid = guid_for('update', oid)
        pre = [{'m_FileID': 0, 'm_PathID': p} for p in scripts + [obj.path_id]]
        container.append((guid, {'preloadIndex': len(preload), 'preloadSize': len(pre), 'asset': {'m_FileID': 0, 'm_PathID': obj.path_id}}))
        preload += pre
        built.append((oid, guid, obj.path_id))
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
    externals = [f.externals for f in env.file.files.values() if hasattr(f, 'objects')][0]
    print('  %s: %s, platform %s, container %d, dangling %d, %d bytes, inner %s, external files %s' % (os.path.basename(path), kinds, plat, len(abt['m_Container']), len(dangling), os.path.getsize(path), names, [e.path for e in externals]))
    for t in want_types:
        assert t in kinds, t
    assert not dangling


_emu = []


def official_compiled(official_id):
    """The official card's own design compiled the way the server compiles an update to it (the server's
    _creator_update_base), so the update's changes can be told apart from what the compiler does not write.
    Needs WF_SERVER (the server code folder) and WF_CONTENT (its content folder with creator-official.json and
    creator-native.json); without them None (the update's whole rules fields are taken)."""
    server, content = os.environ.get('WF_SERVER'), os.environ.get('WF_CONTENT')
    if not server or not content:
        return None
    if not _emu:
        sys.path.insert(0, server)
        import playfab_emu
        emu = object.__new__(playfab_emu.PlayFabEmulator)
        emu.content_root = content
        _emu.append(emu)
    return _emu[0]._creator_update_base(official_id)


def main():
    args = sys.argv[1:]
    art_dir, version = None, 1
    if '--art' in args:
        i = args.index('--art'); art_dir = args[i + 1]; del args[i:i + 2]
    if '--version' in args:
        i = args.index('--version'); version = int(args[i + 1]); del args[i:i + 2]
    out, files = args[0], args[1:]
    os.makedirs(out, exist_ok=True)
    cards, updates = [], []
    for n, f in enumerate(files):
        c = json.load(open(f, encoding='utf-8'))
        if c.get('status') != 'approved':
            print('skipped (not approved):', f); continue
        if not isinstance(c.get('native'), dict):
            raise SystemExit('no compiled layout in ' + f)
        if c.get('updates'):
            # an update to an official card: built from that card, played in its place only in the custom-card modes
            if any(u['updates'] == c['updates'] for u in updates):
                raise SystemExit('two approved updates to %s' % c['updates'])
            updates.append({'id': c['id'], 'updates': c['updates'], 'text': c.get('text', ''), 'native': c['native'],
                            'base': official_compiled(c['updates']) or c.get('nativeBase')})
            continue
        cards.append({'id': c['id'], 'name': c.get('name', ''), 'text': c.get('text', ''), 'lore': c.get('lore', ''),
                      'native': c['native'], 'shortId': SHORT_ID_BASE + int(c['id'].split('-')[-1])})
    if not cards and not updates:
        raise SystemExit('no approved cards')
    art_dir = art_dir or os.path.join(os.path.dirname(os.path.abspath(files[0])), 'art')
    art_guids = {c['id']: guid_for('art', c['id']) for c in cards}
    manifest = {'version': version, 'cards': [], 'text': {}, 'updates': [], 'files': {}}
    for c in cards:
        manifest['cards'].append({'id': c['id'], 'name': c['name'], 'guid': guid_for('card', c['id']), 'art': art_guids[c['id']], 'shortId': c['shortId'],
                                  'rarity': c['native'].get('cardRarity', 1), 'army': c['native'].get('cardArmy', 0)})
        # English from the design; other languages fall back to it until the creator site carries translations
        manifest['text'][c['id']] = {'en': {'name': c['name'], 'text': markup(c['text']), 'lore': c['lore']}}
    for u in updates:
        # the rules text in the language it was written in (English until translations exist); empty = the official text
        manifest['updates'].append({'id': u['updates'], 'design': u['id'], 'guid': guid_for('update', u['updates']),
                                    'text': {'en': {'text': markup(u['text'])}} if u['text'].strip() else {}})
    for platform in ('pc', 'android'):
        p1, _ = build_cards(cards, art_guids, out, platform, updates)
        for entry, u in zip(manifest['updates'], updates):
            entry['changed'], entry['official'] = u['changed'], u['official']
        # no new cards (updates only): no pictures, so no art bundle
        p2 = build_art(cards, art_dir, out, platform)[0] if cards else None
        manifest['files'][platform] = {os.path.basename(p): {'size': os.path.getsize(p), 'sha256': sha256(p)} for p in (p1, p2) if p}
        print(platform + ':')
        # the card scripts are in the bundle (the kit's allcards build) or in the game's script bundle (its served allcards)
        verify(p1, ('MonoBehaviour',))
        if p2:
            verify(p2, ('Texture2D', 'Sprite'))
    json.dump(manifest, open(os.path.join(out, 'custom-tester.json'), 'w', encoding='utf-8'), indent=1, ensure_ascii=False)
    print('cards:', ', '.join('%s (%s)' % (c['id'], c['name']) for c in cards) or 'none')
    print('updates:', ', '.join('%s (%s)' % (u['updates'], u['id']) for u in updates) or 'none')


if __name__ == '__main__':
    main()
